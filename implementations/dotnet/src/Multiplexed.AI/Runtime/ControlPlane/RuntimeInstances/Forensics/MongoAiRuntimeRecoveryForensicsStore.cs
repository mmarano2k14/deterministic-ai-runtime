using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Stores.Mongo;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Stores runtime recovery forensics records in MongoDB.
    /// </summary>
    public sealed class MongoAiRuntimeRecoveryForensicsStore : IAiRuntimeRecoveryForensicsStore
    {
        private readonly IMongoCollection<MongoAiRuntimeRecoveryForensicsDocument> _collection;
        private readonly AiRuntimeRecoveryForensicsMongoOptions _options;
        private readonly SemaphoreSlim _indexInitializationLock = new(1, 1);
        private bool _indexesInitialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeRecoveryForensicsStore"/> class.
        /// </summary>
        /// <param name="database">The MongoDB database.</param>
        /// <param name="options">The MongoDB runtime recovery forensics options.</param>
        public MongoAiRuntimeRecoveryForensicsStore(
            IMongoDatabase database,
            IOptions<AiRuntimeRecoveryForensicsMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Value;
            _collection = database.GetCollection<MongoAiRuntimeRecoveryForensicsDocument>(_options.CollectionName);
        }

        /// <inheritdoc />
        public async Task UpsertAsync(
            AiRuntimeRecoveryForensicsRecord record,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Identity.ForensicsId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Identity.ExecutionId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var createdAtUtc = record.CreatedAtUtc == default ? now : record.CreatedAtUtc;
            var updatedAtUtc = record.UpdatedAtUtc == default ? now : record.UpdatedAtUtc;

            var normalized = record with
            {
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = updatedAtUtc,
                Events = NormalizeEvents(record.Events)
            };

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Id,
                normalized.Identity.ForensicsId);

            var existing = await _collection
                .Find(filter)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                normalized = normalized with
                {
                    CreatedAtUtc = existing.Record.CreatedAtUtc == default ? normalized.CreatedAtUtc : existing.Record.CreatedAtUtc,
                    Events = NormalizeEvents(existing.Record.Events.Concat(normalized.Events).ToList())
                };
            }

            var document = MongoAiRuntimeRecoveryForensicsDocument.FromRecord(normalized);

            await _collection.ReplaceOneAsync(
                    filter,
                    document,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task AppendEventAsync(
            string forensicsId,
            AiRuntimeRecoveryForensicsEvent evt,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);
            ArgumentNullException.ThrowIfNull(evt);
            ArgumentException.ThrowIfNullOrWhiteSpace(evt.EventId);
            ArgumentException.ThrowIfNullOrWhiteSpace(evt.ForensicsId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            var normalizedEvent = evt with
            {
                TimestampUtc = evt.TimestampUtc == default ? now : evt.TimestampUtc
            };

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Id,
                forensicsId);

            var existing = await _collection
                .Find(filter)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var record = existing?.Record ?? new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = normalizedEvent.ExecutionId ?? string.Empty,
                    SharedRunId = normalizedEvent.SharedRunId
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Events = Array.Empty<AiRuntimeRecoveryForensicsEvent>()
            };

            var normalized = record with
            {
                UpdatedAtUtc = now,
                Events = NormalizeEvents(record.Events.Concat(new[] { normalizedEvent }).ToList())
            };

            var document = MongoAiRuntimeRecoveryForensicsDocument.FromRecord(normalized);

            await _collection.ReplaceOneAsync(
                    filter,
                    document,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeRecoveryForensicsRecord?> GetByForensicsIdAsync(
            string forensicsId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Id,
                forensicsId);

            var document = await _collection
                .Find(filter)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return document?.Record;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByExecutionIdAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Record.Identity.ExecutionId,
                executionId);

            var documents = await _collection
                .Find(filter)
                .SortByDescending(x => x.Record.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return documents.Select(x => x.Record).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListBySharedRunIdAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Record.Identity.SharedRunId,
                sharedRunId);

            var documents = await _collection
                .Find(filter)
                .SortByDescending(x => x.Record.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return documents.Select(x => x.Record).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeInstanceIdAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Or(
                Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(x => x.Record.Failure!.FailedRuntimeInstanceId, runtimeInstanceId),
                Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(x => x.Record.Replacement!.ReplacementRuntimeInstanceId, runtimeInstanceId));

            var documents = await _collection
                .Find(filter)
                .SortByDescending(x => x.Record.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return documents.Select(x => x.Record).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeFailureIncidentIdAsync(
            string runtimeFailureIncidentId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeFailureIncidentId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Eq(
                x => x.Record.Failure!.RuntimeFailureIncidentId,
                runtimeFailureIncidentId);

            var documents = await _collection
                .Find(filter)
                .SortByDescending(x => x.Record.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return documents.Select(x => x.Record).ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var safeLimit = Math.Max(1, limit);

            var documents = await _collection
                .Find(Builders<MongoAiRuntimeRecoveryForensicsDocument>.Filter.Empty)
                .SortByDescending(x => x.Record.CreatedAtUtc)
                .Limit(safeLimit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return documents.Select(x => x.Record).ToList();
        }

        /// <summary>
        /// Ensures MongoDB indexes required by recovery forensics queries.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when indexes have been ensured.</returns>
        private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
        {
            if (_indexesInitialized || !_options.EnsureIndexes)
            {
                return;
            }

            await _indexInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (_indexesInitialized)
                {
                    return;
                }

                await MongoRuntimeResilience.ExecuteInfrastructureAsync(
                    async token =>
                    {
                        var models = new[]
                        {
                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Id),
                                new CreateIndexOptions { Name = "ux_id", Unique = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Record.Identity.ExecutionId),
                                new CreateIndexOptions { Name = "ix_record_identity_executionId" }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Record.Identity.SharedRunId),
                                new CreateIndexOptions { Name = "ix_record_identity_sharedRunId", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys
                                    .Ascending(x => x.Record.Identity.TenantId)
                                    .Descending(x => x.Record.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_record_identity_tenantId_createdAtUtc", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys
                                    .Ascending(x => x.Record.Identity.ControlPlaneId)
                                    .Descending(x => x.Record.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_record_identity_controlPlaneId_createdAtUtc", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Record.Failure!.FailedRuntimeInstanceId),
                                new CreateIndexOptions { Name = "ix_record_failure_failedRuntimeInstanceId", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Record.Failure!.RuntimeFailureIncidentId),
                                new CreateIndexOptions { Name = "ix_record_failure_runtimeFailureIncidentId", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys.Ascending(x => x.Record.Replacement!.ReplacementRuntimeInstanceId),
                                new CreateIndexOptions { Name = "ix_record_replacement_replacementRuntimeInstanceId", Sparse = true }),

                            new CreateIndexModel<MongoAiRuntimeRecoveryForensicsDocument>(
                                Builders<MongoAiRuntimeRecoveryForensicsDocument>.IndexKeys
                                    .Ascending(x => x.Record.Recovery!.RecoveryMode)
                                    .Ascending(x => x.Record.Recovery!.Outcome)
                                    .Descending(x => x.Record.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_record_recovery_mode_outcome_createdAtUtc", Sparse = true })
                        };

                        await _collection.Indexes.CreateManyAsync(models, token).ConfigureAwait(false);
                    },
                    "runtime-recovery-forensics-indexes",
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _indexesInitialized = true;
            }
            finally
            {
                _indexInitializationLock.Release();
            }
        }

        /// <summary>
        /// Normalizes recovery forensics events by event identifier and timestamp.
        /// </summary>
        /// <param name="events">The events to normalize.</param>
        /// <returns>The normalized events.</returns>
        private static IReadOnlyList<AiRuntimeRecoveryForensicsEvent> NormalizeEvents(
            IReadOnlyList<AiRuntimeRecoveryForensicsEvent> events)
        {
            return events
                .GroupBy(x => x.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        }
    }
}
