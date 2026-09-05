using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.AI.Runtime.Observability.Performance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Stores.Mongo;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Stores append-only runtime lifecycle events in MongoDB.
    /// </summary>
    public sealed class MongoAiRuntimeLifecycleJournal : IAiRuntimeLifecycleJournal
    {
        private readonly IMongoCollection<MongoAiRuntimeLifecycleEventDocument> _collection;
        private readonly AiRuntimeLifecycleJournalMongoOptions _options;
        private readonly SemaphoreSlim _indexInitializationLock = new(1, 1);
        private bool _indexesInitialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeLifecycleJournal"/> class.
        /// </summary>
        public MongoAiRuntimeLifecycleJournal(
            IMongoDatabase database,
            IOptions<AiRuntimeLifecycleJournalMongoOptions> options)
        {
            ArgumentNullException.ThrowIfNull(database);
            ArgumentNullException.ThrowIfNull(options);

            _options = options.Value;
            ArgumentException.ThrowIfNullOrWhiteSpace(_options.CollectionName);

            _collection = database.GetCollection<MongoAiRuntimeLifecycleEventDocument>(
                _options.CollectionName);
        }

        /// <inheritdoc />
        public async Task AppendAsync(
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            var normalized = AiRuntimeLifecycleEventNormalization.Normalize(lifecycleEvent);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var appendMeasurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.RuntimeLifecycleAppend,
                AiMongoAttributionCommands.Insert,
                requestedDocuments: 1);

            try
            {
                await _collection
                    .InsertOneAsync(
                        MongoAiRuntimeLifecycleEventDocument.FromEvent(normalized),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                appendMeasurement.Succeed();
            }
            catch (MongoException exception) when (IsDuplicateKey(exception))
            {
                appendMeasurement.Fail();
                var existing = await GetByEventIdCoreAsync(
                        normalized.EventId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null &&
                    AiRuntimeLifecycleEventNormalization.AreEquivalent(existing, normalized))
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Runtime lifecycle event '{normalized.EventId}' already exists with a different immutable payload.",
                    exception);
            }
            catch (OperationCanceledException)
            {
                appendMeasurement.Cancel();
                throw;
            }
            catch
            {
                appendMeasurement.Fail();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(
            string eventId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            return await GetByEventIdCoreAsync(eventId, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByControlPlaneIdAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.ControlPlaneId,
                    controlPlaneId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.PoolId,
                    poolId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.HostId,
                    hostId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByKubernetesPodUidAsync(
            string kubernetesPodUid,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kubernetesPodUid);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.KubernetesPodUid,
                    kubernetesPodUid),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeInstanceIdAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.RuntimeInstanceId,
                    runtimeInstanceId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeFailureIncidentIdAsync(
            string runtimeFailureIncidentId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeFailureIncidentId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.RuntimeFailureIncidentId,
                    runtimeFailureIncidentId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListBySharedRunIdAsync(
            string tenantId,
            string sharedRunId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            var filter = Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.And(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.TenantId,
                    tenantId),
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.SharedRunId,
                    sharedRunId));

            return QueryAsync(filter, cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByExecutionIdAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.ExecutionId,
                    executionId),
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByCorrelationIdAsync(
            string correlationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

            return QueryAsync(
                Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                    document => document.Event.CorrelationId,
                    correlationId),
                cancellationToken);
        }

        private async Task<AiRuntimeLifecycleEvent?> GetByEventIdCoreAsync(
            string eventId,
            CancellationToken cancellationToken)
        {
            var filter = Builders<MongoAiRuntimeLifecycleEventDocument>.Filter.Eq(
                document => document.Id,
                eventId);

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.RuntimeLifecycleQuery,
                AiMongoAttributionCommands.Find);
            try
            {
                var document = await _collection
                    .Find(filter)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                measurement.Succeed(document is null ? 0 : 1);
                return document?.Event;
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
        }

        private async Task<IReadOnlyList<AiRuntimeLifecycleEvent>> QueryAsync(
            FilterDefinition<MongoAiRuntimeLifecycleEventDocument> filter,
            CancellationToken cancellationToken)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var sort = Builders<MongoAiRuntimeLifecycleEventDocument>.Sort
                .Ascending(document => document.Event.TimestampUtc)
                .Ascending(document => document.Id);

            var measurement = AiMongoAttributionDiagnostics.StartOperation(
                AiMongoAttributionOperations.RuntimeLifecycleQuery,
                AiMongoAttributionCommands.Find);
            try
            {
                var documents = await _collection
                    .Find(filter)
                    .Sort(sort)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                measurement.Succeed(documents.Count);
                return documents.Select(document => document.Event).ToList();
            }
            catch (OperationCanceledException) { measurement.Cancel(); throw; }
            catch { measurement.Fail(); throw; }
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
                                CreateIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.ControlPlaneId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_controlPlaneId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.PoolId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_poolId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.HostId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_hostId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.KubernetesPodUid)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_kubernetesPodUid_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.RuntimeInstanceId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_runtimeInstanceId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.RuntimeFailureIncidentId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_incidentId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.TenantId)
                                        .Ascending(document => document.Event.SharedRunId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_tenantId_sharedRunId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.ExecutionId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_executionId_timestamp_eventId"),

                                CreateSparseIndex(
                                    Builders<MongoAiRuntimeLifecycleEventDocument>.IndexKeys
                                        .Ascending(document => document.Event.CorrelationId)
                                        .Ascending(document => document.Event.TimestampUtc)
                                        .Ascending(document => document.Id),
                                    "ix_event_correlationId_timestamp_eventId")
                            };

                            await _collection.Indexes
                                .CreateManyAsync(models, token)
                                .ConfigureAwait(false);
                        },
                        "runtime-lifecycle-journal-indexes",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                _indexesInitialized = true;
            }
            finally
            {
                _indexInitializationLock.Release();
            }
        }

        private static CreateIndexModel<MongoAiRuntimeLifecycleEventDocument> CreateIndex(
            IndexKeysDefinition<MongoAiRuntimeLifecycleEventDocument> keys,
            string name)
        {
            return new CreateIndexModel<MongoAiRuntimeLifecycleEventDocument>(
                keys,
                new CreateIndexOptions { Name = name });
        }

        private static CreateIndexModel<MongoAiRuntimeLifecycleEventDocument> CreateSparseIndex(
            IndexKeysDefinition<MongoAiRuntimeLifecycleEventDocument> keys,
            string name)
        {
            return new CreateIndexModel<MongoAiRuntimeLifecycleEventDocument>(
                keys,
                new CreateIndexOptions
                {
                    Name = name,
                    Sparse = true
                });
        }

        private static bool IsDuplicateKey(MongoException exception)
        {
            if (exception is MongoCommandException commandException)
            {
                return commandException.Code == 11000 ||
                       string.Equals(
                           commandException.CodeName,
                           "DuplicateKey",
                           StringComparison.OrdinalIgnoreCase) ||
                       commandException.Message.Contains(
                           "E11000",
                           StringComparison.OrdinalIgnoreCase);
            }

            if (exception is MongoWriteException writeException)
            {
                return writeException.WriteError?.Category == ServerErrorCategory.DuplicateKey ||
                       writeException.WriteError?.Code == 11000 ||
                       writeException.Message.Contains(
                           "E11000",
                           StringComparison.OrdinalIgnoreCase);
            }

            return exception.Message.Contains(
                "E11000",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
