using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Tests the MongoDB-backed runtime lifecycle journal.
    /// </summary>
    public sealed class MongoAiRuntimeLifecycleJournalTests : IAsyncLifetime
    {
        private readonly MongoClient _client;
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly IOptions<AiRuntimeLifecycleJournalMongoOptions> _options;
        private readonly string _databaseName;

        public MongoAiRuntimeLifecycleJournalTests()
        {
            var connectionString = Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";

            _databaseName = $"multiplexed_runtime_lifecycle_{Guid.NewGuid():N}";
            _client = new MongoClient(connectionString);
            _database = _client.GetDatabase(_databaseName);
            _options = Options.Create(
                new AiRuntimeLifecycleJournalMongoOptions
                {
                    CollectionName = "runtime_lifecycle_events_tests",
                    EnsureIndexes = true
                });
            _collection = _database.GetCollection<BsonDocument>(_options.Value.CollectionName);
        }

        public async Task InitializeAsync()
        {
            await _collection
                .DeleteManyAsync(Builders<BsonDocument>.Filter.Empty)
                .ConfigureAwait(false);
        }

        public async Task DisposeAsync()
        {
            await _client.DropDatabaseAsync(_databaseName).ConfigureAwait(false);
        }

        [Fact]
        public async Task AppendAsync_Should_Remain_Durable_Across_Store_Instances()
        {
            var firstJournal = new MongoAiRuntimeLifecycleJournal(_database, _options);
            var timestamp = DateTimeOffset.UtcNow;

            await firstJournal.AppendAsync(CreateEvent("event-2", timestamp.AddSeconds(1)));
            await firstJournal.AppendAsync(CreateEvent("event-1", timestamp));

            var secondJournal = new MongoAiRuntimeLifecycleJournal(_database, _options);
            var events = await secondJournal.ListByControlPlaneIdAsync("control-plane-1");

            Assert.Equal(new[] { "event-1", "event-2" }, events.Select(x => x.EventId));
            Assert.All(events, lifecycleEvent => Assert.Equal("pod-uid-1", lifecycleEvent.KubernetesPodUid));
        }

        [Fact]
        public async Task AppendAsync_Should_Be_Idempotent_But_Reject_Conflicting_Payload()
        {
            var journal = new MongoAiRuntimeLifecycleJournal(_database, _options);
            var lifecycleEvent = CreateEvent("event-idempotent", DateTimeOffset.UtcNow);

            await journal.AppendAsync(lifecycleEvent);
            await journal.AppendAsync(lifecycleEvent);

            var events = await journal.ListByControlPlaneIdAsync("control-plane-1");

            Assert.Single(events);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.AppendAsync(lifecycleEvent with
                {
                    RuntimeInstanceId = "runtime-conflict"
                }));

            Assert.Contains("different immutable payload", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ListBySharedRunIdAsync_Should_Enforce_Tenant_Scope()
        {
            var journal = new MongoAiRuntimeLifecycleJournal(_database, _options);
            var timestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(CreateEvent("event-tenant-1", timestamp));
            await journal.AppendAsync(CreateEvent("event-tenant-2", timestamp.AddSeconds(1)) with
            {
                TenantId = "tenant-2"
            });

            var events = await journal.ListBySharedRunIdAsync("tenant-1", "shared-run-1");

            Assert.Single(events);
            Assert.Equal("event-tenant-1", events[0].EventId);
        }

        [Fact]
        public async Task AppendAsync_Should_Create_Required_Query_Indexes()
        {
            var journal = new MongoAiRuntimeLifecycleJournal(_database, _options);

            await journal.AppendAsync(CreateEvent("event-indexes", DateTimeOffset.UtcNow));

            var indexes = await _collection.Indexes.ListAsync();
            var documents = await indexes.ToListAsync();
            var names = documents
                .Select(document => document["name"].AsString)
                .ToArray();

            Assert.Contains("ix_event_controlPlaneId_timestamp_eventId", names);
            Assert.Contains("ix_event_poolId_timestamp_eventId", names);
            Assert.Contains("ix_event_hostId_timestamp_eventId", names);
            Assert.Contains("ix_event_kubernetesPodUid_timestamp_eventId", names);
            Assert.Contains("ix_event_runtimeInstanceId_timestamp_eventId", names);
            Assert.Contains("ix_event_incidentId_timestamp_eventId", names);
            Assert.Contains("ix_event_tenantId_sharedRunId_timestamp_eventId", names);
            Assert.Contains("ix_event_executionId_timestamp_eventId", names);
            Assert.Contains("ix_event_correlationId_timestamp_eventId", names);
        }

        private static AiRuntimeLifecycleEvent CreateEvent(
            string eventId,
            DateTimeOffset timestampUtc)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = AiRuntimeLifecycleEventType.RuntimeReplacementRegistered,
                TimestampUtc = timestampUtc,
                ControlPlaneId = "control-plane-1",
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                ProviderName = "grpc",
                PoolId = "pool-1",
                HostId = "pod-uid-1",
                KubernetesPodUid = "pod-uid-1",
                KubernetesNamespace = "ai-runtime",
                KubernetesPodName = "runtime-pool-pod-1",
                KubernetesNodeName = "minikube",
                RuntimeInstanceId = "runtime-1",
                RuntimeId = "runtime-local-1",
                TenantId = "tenant-1",
                SharedRunId = "shared-run-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                RuntimeFailureIncidentId = "incident-1",
                LedgerEntryId = "ledger-1",
                ForensicsId = "forensics-1",
                CorrelationId = "correlation-1",
                CausationId = "causation-1"
            };
        }
    }
}
