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
        private readonly IMongoCollection<AiRuntimeRecoveryForensicsRecord> _collection;
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
            _collection = database.GetCollection<AiRuntimeRecoveryForensicsRecord>(_options.CollectionName);
        }

        /// <inheritdoc />
        public async Task UpsertAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default)
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

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.ForensicsId,
                normalized.Identity.ForensicsId);

            await _collection.ReplaceOneAsync(
                    filter,
                    normalized,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task AppendEventAsync(string forensicsId, AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default)
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

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.ForensicsId,
                forensicsId);

            var update = Builders<AiRuntimeRecoveryForensicsRecord>.Update
                .SetOnInsert(
                    x => x.Identity,
                    new AiRuntimeRecoveryForensicsIdentity
                    {
                        ForensicsId = forensicsId,
                        ExecutionId = normalizedEvent.ExecutionId ?? string.Empty,
                        SharedRunId = normalizedEvent.SharedRunId
                    })
                .SetOnInsert(x => x.CreatedAtUtc, now)
                .Set(x => x.UpdatedAtUtc, now)
                .AddToSet(x => x.Events, normalizedEvent);

            await _collection.UpdateOneAsync(
                    filter,
                    update,
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeRecoveryForensicsRecord?> GetByForensicsIdAsync(string forensicsId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(forensicsId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.ForensicsId,
                forensicsId);

            return await _collection
                .Find(filter)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.ExecutionId,
                executionId);

            return await _collection
                .Find(filter)
                .SortByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListBySharedRunIdAsync(string sharedRunId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Identity.SharedRunId,
                sharedRunId);

            return await _collection
                .Find(filter)
                .SortByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeInstanceIdAsync(string runtimeInstanceId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Or(
                Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(x => x.Failure!.FailedRuntimeInstanceId, runtimeInstanceId),
                Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(x => x.Replacement!.ReplacementRuntimeInstanceId, runtimeInstanceId));

            return await _collection
                .Find(filter)
                .SortByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeFailureIncidentIdAsync(string runtimeFailureIncidentId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeFailureIncidentId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var filter = Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Eq(
                x => x.Failure!.RuntimeFailureIncidentId,
                runtimeFailureIncidentId);

            return await _collection
                .Find(filter)
                .SortByDescending(x => x.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var safeLimit = Math.Max(1, limit);

            return await _collection
                .Find(Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Empty)
                .SortByDescending(x => x.CreatedAtUtc)
                .Limit(safeLimit)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

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
                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Identity.ForensicsId),
                                new CreateIndexOptions { Name = "ux_identity_forensicsId", Unique = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Identity.ExecutionId),
                                new CreateIndexOptions { Name = "ix_identity_executionId" }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Identity.SharedRunId),
                                new CreateIndexOptions { Name = "ix_identity_sharedRunId", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys
                                    .Ascending(x => x.Identity.TenantId)
                                    .Descending(x => x.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_identity_tenantId_createdAtUtc", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys
                                    .Ascending(x => x.Identity.ControlPlaneId)
                                    .Descending(x => x.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_identity_controlPlaneId_createdAtUtc", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Failure!.FailedRuntimeInstanceId),
                                new CreateIndexOptions { Name = "ix_failure_failedRuntimeInstanceId", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Failure!.RuntimeFailureIncidentId),
                                new CreateIndexOptions { Name = "ix_failure_runtimeFailureIncidentId", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys.Ascending(x => x.Replacement!.ReplacementRuntimeInstanceId),
                                new CreateIndexOptions { Name = "ix_replacement_replacementRuntimeInstanceId", Sparse = true }),

                            new CreateIndexModel<AiRuntimeRecoveryForensicsRecord>(
                                Builders<AiRuntimeRecoveryForensicsRecord>.IndexKeys
                                    .Ascending(x => x.Recovery!.RecoveryMode)
                                    .Ascending(x => x.Recovery!.Outcome)
                                    .Descending(x => x.CreatedAtUtc),
                                new CreateIndexOptions { Name = "ix_recovery_mode_outcome_createdAtUtc", Sparse = true })
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

        private static IReadOnlyList<AiRuntimeRecoveryForensicsEvent> NormalizeEvents(IReadOnlyList<AiRuntimeRecoveryForensicsEvent> events)
        {
            return events
                .GroupBy(x => x.EventId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.TimestampUtc)
                .ToList();
        }
    }
}