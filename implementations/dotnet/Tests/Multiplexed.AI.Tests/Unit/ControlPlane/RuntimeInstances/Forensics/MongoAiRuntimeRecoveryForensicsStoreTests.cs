using FluentAssertions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Tests the MongoDB runtime recovery forensics store.
    /// </summary>
    public sealed class MongoAiRuntimeRecoveryForensicsStoreTests
    {
        private const string MongoConnectionStringEnvironmentVariable = "MULTIPLEXED_TEST_MONGO_CONNECTION_STRING";

        private const string MongoDatabaseEnvironmentVariable = "MULTIPLEXED_TEST_MONGO_DATABASE";

        /// <summary>
        /// Verifies that the Mongo store can upsert and retrieve a recovery forensics record by forensics id.
        /// </summary>
        [Fact]
        public async Task UpsertAsync_Should_Persist_Record_By_ForensicsId()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                var record = CreateRecord("mongo-forensics-001", "mongo-execution-001", "mongo-shared-run-001");

                await store.UpsertAsync(record);

                var loaded = await store.GetByForensicsIdAsync("mongo-forensics-001");

                loaded.Should().NotBeNull();
                loaded!.Identity.ForensicsId.Should().Be("mongo-forensics-001");
                loaded.Identity.ExecutionId.Should().Be("mongo-execution-001");
                loaded.Identity.SharedRunId.Should().Be("mongo-shared-run-001");
                loaded.Artifacts.Restored.Should().Contain(AiRuntimeRecoveryArtifactName.DurableExecutionId);
                loaded.Artifacts.Recreated.Should().Contain(AiRuntimeRecoveryArtifactName.ReplacementLocalRunId);
                loaded.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.OldLease);
            }
        }

        /// <summary>
        /// Verifies that the Mongo store can append recovery forensics events.
        /// </summary>
        [Fact]
        public async Task AppendEventAsync_Should_Persist_Event_Timeline()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-002", "mongo-execution-002", "mongo-shared-run-002"));

                await store.AppendEventAsync("mongo-forensics-002", CreateEvent("mongo-event-002-a", "mongo-forensics-002", AiEngineEvents.Recovery.SharedRunRequeuedForResume, "mongo-execution-002", "mongo-shared-run-002", "failed-local-run-002", "runtime-1", DateTimeOffset.UtcNow.AddSeconds(1)));
                await store.AppendEventAsync("mongo-forensics-002", CreateEvent("mongo-event-002-b", "mongo-forensics-002", AiEngineEvents.Recovery.ReplacementLocalRunRegistered, "mongo-execution-002", "mongo-shared-run-002", "replacement-local-run-002", "runtime-2", DateTimeOffset.UtcNow.AddSeconds(2)));

                var loaded = await store.GetByForensicsIdAsync("mongo-forensics-002");

                loaded.Should().NotBeNull();
                loaded!.Events.Should().HaveCount(2);
                loaded.Events.Select(x => x.EventType).Should().Contain(AiEngineEvents.Recovery.SharedRunRequeuedForResume);
                loaded.Events.Select(x => x.EventType).Should().Contain(AiEngineEvents.Recovery.ReplacementLocalRunRegistered);
            }
        }

        /// <summary>
        /// Verifies that records can be queried by execution id.
        /// </summary>
        [Fact]
        public async Task ListByExecutionIdAsync_Should_Return_Matching_Records()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-003-a", "mongo-execution-003", "mongo-shared-run-003-a"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-003-b", "mongo-execution-003", "mongo-shared-run-003-b"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-003-c", "mongo-execution-other", "mongo-shared-run-other"));

                var records = await store.ListByExecutionIdAsync("mongo-execution-003");

                records.Should().HaveCount(2);
                records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("mongo-forensics-003-a", "mongo-forensics-003-b");
            }
        }

        /// <summary>
        /// Verifies that records can be queried by shared run id.
        /// </summary>
        [Fact]
        public async Task ListBySharedRunIdAsync_Should_Return_Matching_Records()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-004-a", "mongo-execution-004-a", "mongo-shared-run-004"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-004-b", "mongo-execution-004-b", "mongo-shared-run-004"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-004-c", "mongo-execution-004-c", "mongo-shared-run-other"));

                var records = await store.ListBySharedRunIdAsync("mongo-shared-run-004");

                records.Should().HaveCount(2);
                records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("mongo-forensics-004-a", "mongo-forensics-004-b");
            }
        }

        /// <summary>
        /// Verifies that records can be queried by failed or replacement runtime instance id.
        /// </summary>
        [Fact]
        public async Task ListByRuntimeInstanceIdAsync_Should_Return_Failed_And_Replacement_Runtime_Matches()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-005-a", "mongo-execution-005-a", "mongo-shared-run-005-a", failedRuntimeInstanceId: "runtime-1", replacementRuntimeInstanceId: "runtime-2"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-005-b", "mongo-execution-005-b", "mongo-shared-run-005-b", failedRuntimeInstanceId: "runtime-3", replacementRuntimeInstanceId: "runtime-1"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-005-c", "mongo-execution-005-c", "mongo-shared-run-005-c", failedRuntimeInstanceId: "runtime-4", replacementRuntimeInstanceId: "runtime-5"));

                var records = await store.ListByRuntimeInstanceIdAsync("runtime-1");

                records.Should().HaveCount(2);
                records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("mongo-forensics-005-a", "mongo-forensics-005-b");
            }
        }

        /// <summary>
        /// Verifies that records can be queried by runtime failure incident id.
        /// </summary>
        [Fact]
        public async Task ListByRuntimeFailureIncidentIdAsync_Should_Return_All_Records_For_Same_Runtime_Failure()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-006-a", "mongo-execution-006-a", "mongo-shared-run-006-a", runtimeFailureIncidentId: "incident-runtime-1"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-006-b", "mongo-execution-006-b", "mongo-shared-run-006-b", runtimeFailureIncidentId: "incident-runtime-1"));
                await store.UpsertAsync(CreateRecord("mongo-forensics-006-c", "mongo-execution-006-c", "mongo-shared-run-006-c", runtimeFailureIncidentId: "incident-runtime-2"));

                var records = await store.ListByRuntimeFailureIncidentIdAsync("incident-runtime-1");

                records.Should().HaveCount(2);
                records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("mongo-forensics-006-a", "mongo-forensics-006-b");
            }
        }

        /// <summary>
        /// Verifies that recent records are returned newest first and limited.
        /// </summary>
        [Fact]
        public async Task ListRecentAsync_Should_Return_Limited_Records_Newest_First()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-007-a", "mongo-execution-007-a", "mongo-shared-run-007-a", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-3)));
                await store.UpsertAsync(CreateRecord("mongo-forensics-007-b", "mongo-execution-007-b", "mongo-shared-run-007-b", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-2)));
                await store.UpsertAsync(CreateRecord("mongo-forensics-007-c", "mongo-execution-007-c", "mongo-shared-run-007-c", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

                var records = await store.ListRecentAsync(2);

                records.Should().HaveCount(2);
                records.Select(x => x.Identity.ForensicsId).Should().ContainInOrder("mongo-forensics-007-c", "mongo-forensics-007-b");
            }
        }

        /// <summary>
        /// Verifies that the store creates query indexes.
        /// </summary>
        [Fact]
        public async Task UpsertAsync_Should_Create_Query_Indexes()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var store = fixture.CreateStore();

                await store.UpsertAsync(CreateRecord("mongo-forensics-008", "mongo-execution-008", "mongo-shared-run-008"));

                var indexes = await fixture.Collection.Indexes.ListAsync();
                var indexDocuments = await indexes.ToListAsync();
                var indexNames = indexDocuments.Select(x => x.GetValue("name").AsString).ToList();

                indexNames.Should().Contain("ux_identity_forensicsId");
                indexNames.Should().Contain("ix_identity_executionId");
                indexNames.Should().Contain("ix_identity_sharedRunId");
                indexNames.Should().Contain("ix_failure_failedRuntimeInstanceId");
                indexNames.Should().Contain("ix_failure_runtimeFailureIncidentId");
                indexNames.Should().Contain("ix_replacement_replacementRuntimeInstanceId");
            }
        }

        /// <summary>
        /// Verifies that concurrent event appends do not lose uniquely identified
        /// recovery timeline events for the same forensics record.
        /// </summary>
        [Fact]
        public async Task AppendEventAsync_Should_Not_Lose_Unique_Events_When_Appended_Concurrently()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                const int eventCount = 100;

                var store = fixture.CreateStore();
                var forensicsId = $"mongo-forensics-concurrent-{Guid.NewGuid():N}";
                var executionId = $"mongo-execution-concurrent-{Guid.NewGuid():N}";
                var sharedRunId = $"mongo-shared-run-concurrent-{Guid.NewGuid():N}";
                var startedAtUtc = DateTimeOffset.UtcNow;

                await store.UpsertAsync(
                    CreateRecord(
                        forensicsId,
                        executionId,
                        sharedRunId));

                var events = Enumerable
                    .Range(0, eventCount)
                    .Select(index =>
                        CreateEvent(
                            $"mongo-event-concurrent-{index:D3}",
                            forensicsId,
                            index % 2 == 0
                                ? AiEngineEvents.Recovery.ResumeContextSeeded
                                : AiEngineEvents.Recovery.DagResumeStarted,
                            executionId,
                            sharedRunId,
                            $"local-run-{index:D3}",
                            $"runtime-{index:D3}",
                            startedAtUtc.AddMilliseconds(index)))
                    .ToArray();

                var startGate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var appendTasks = events
                    .Select(
                        async evt =>
                        {
                            await startGate.Task.ConfigureAwait(false);

                            await store
                                .AppendEventAsync(
                                    forensicsId,
                                    evt)
                                .ConfigureAwait(false);
                        })
                    .ToArray();

                startGate.SetResult(true);

                await Task.WhenAll(appendTasks);

                var loaded = await store.GetByForensicsIdAsync(forensicsId);

                loaded.Should().NotBeNull();

                loaded!.Events
                    .Should()
                    .HaveCount(eventCount);

                loaded.Events
                    .Select(x => x.EventId)
                    .Should()
                    .OnlyHaveUniqueItems()
                    .And
                    .BeEquivalentTo(events.Select(x => x.EventId));
            }
        }

        /// <summary>
        /// Verifies that an upsert based on an older document snapshot does not
        /// overwrite an event appended after that snapshot was read.
        /// </summary>
        [Fact]
        public async Task UpsertAsync_Should_Not_Overwrite_Event_Appended_After_Its_Read()
        {
            var fixture = await TryCreateFixtureAsync();

            if (fixture is null)
            {
                return;
            }

            await using (fixture)
            {
                var connectionString =
                    Environment.GetEnvironmentVariable(
                        MongoConnectionStringEnvironmentVariable);

                connectionString.Should().NotBeNullOrWhiteSpace();

                var forensicsId =
                    $"mongo-forensics-upsert-append-race-{Guid.NewGuid():N}";

                var executionId =
                    $"mongo-execution-upsert-append-race-{Guid.NewGuid():N}";

                var sharedRunId =
                    $"mongo-shared-run-upsert-append-race-{Guid.NewGuid():N}";

                var eventId =
                    $"mongo-event-upsert-append-race-{Guid.NewGuid():N}";

                var initialRecord =
                    CreateRecord(
                        forensicsId,
                        executionId,
                        sharedRunId);

                var normalStore =
                    fixture.CreateStore();

                await normalStore
                    .UpsertAsync(initialRecord)
                    .ConfigureAwait(false);

                var replaceCommandReached =
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                using var releaseReplaceCommand =
                    new ManualResetEventSlim(false);

                var interceptedUpdateCount = 0;

                var blockingClientSettings =
                    MongoClientSettings.FromConnectionString(
                        connectionString!);

                blockingClientSettings.ClusterConfigurator =
                    clusterBuilder =>
                    {
                        clusterBuilder.Subscribe<CommandStartedEvent>(
                            commandEvent =>
                            {
                                if (!string.Equals(
                                        commandEvent.CommandName,
                                        "update",
                                        StringComparison.Ordinal) ||
                                    Interlocked.CompareExchange(
                                        ref interceptedUpdateCount,
                                        1,
                                        0) != 0)
                                {
                                    return;
                                }

                                replaceCommandReached.TrySetResult(true);

                                if (!releaseReplaceCommand.Wait(
                                        TimeSpan.FromSeconds(30)))
                                {
                                    throw new TimeoutException(
                                        "Timed out while waiting to release the blocked MongoDB replace command.");
                                }
                            });
                    };

                var blockingClient =
                    new MongoClient(blockingClientSettings);

                var blockingDatabase =
                    blockingClient.GetDatabase(
                        fixture.DatabaseName);

                var staleUpsertStore =
                    fixture.CreateStore(blockingDatabase);

                var staleRecord =
                    initialRecord with
                    {
                        UpdatedAtUtc =
                            initialRecord.UpdatedAtUtc.AddSeconds(10),

                        Recovery =
                            initialRecord.Recovery! with
                            {
                                Reason =
                                    "stale-upsert-after-document-read"
                            }
                    };

                var staleUpsertTask =
                    staleUpsertStore.UpsertAsync(staleRecord);

                try
                {
                    await replaceCommandReached
                        .Task
                        .WaitAsync(TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);

                    await normalStore
                        .AppendEventAsync(
                            forensicsId,
                            CreateEvent(
                                eventId,
                                forensicsId,
                                AiEngineEvents.Recovery.ResumeContextSeeded,
                                executionId,
                                sharedRunId,
                                "replacement-local-run",
                                "replacement-runtime",
                                DateTimeOffset.UtcNow))
                        .ConfigureAwait(false);
                }
                finally
                {
                    releaseReplaceCommand.Set();
                }

                await staleUpsertTask
                    .ConfigureAwait(false);

                var loaded =
                    await normalStore
                        .GetByForensicsIdAsync(forensicsId)
                        .ConfigureAwait(false);

                loaded.Should().NotBeNull();

                loaded!.Events
                    .Should()
                    .ContainSingle(
                        evt => string.Equals(
                            evt.EventId,
                            eventId,
                            StringComparison.Ordinal));
            }
        }

        /// <summary>
        /// Tries to create a MongoDB fixture.
        /// </summary>
        /// <returns>The fixture when MongoDB test configuration is available; otherwise, null.</returns>
        private static async Task<MongoForensicsTestFixture?> TryCreateFixtureAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(MongoConnectionStringEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            var databaseName = Environment.GetEnvironmentVariable(MongoDatabaseEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(databaseName))
            {
                databaseName = $"multiplexed_ai_tests_{Guid.NewGuid():N}";
            }

            var collectionName = $"ai_runtime_recovery_forensics_tests_{Guid.NewGuid():N}";
            var client = new MongoClient(connectionString);
            var database = client.GetDatabase(databaseName);
            var fixture = new MongoForensicsTestFixture(database, collectionName);

            await database.DropCollectionAsync(collectionName);

            return fixture;
        }

        /// <summary>
        /// Creates a recovery forensics record for tests.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance identifier.</param>
        /// <param name="runtimeFailureIncidentId">The runtime failure incident identifier.</param>
        /// <param name="createdAtUtc">The record creation timestamp.</param>
        /// <returns>The recovery forensics record.</returns>
        private static AiRuntimeRecoveryForensicsRecord CreateRecord(
            string forensicsId,
            string executionId,
            string sharedRunId,
            string failedRuntimeInstanceId = "runtime-1",
            string replacementRuntimeInstanceId = "runtime-2",
            string runtimeFailureIncidentId = "incident-runtime-1",
            DateTimeOffset? createdAtUtc = null)
        {
            var now = createdAtUtc ?? DateTimeOffset.UtcNow;

            return new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = executionId,
                    SharedRunId = sharedRunId,
                    PipelineName = "pipeline-mongo-forensics-test",
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-group-a",
                    ControlPlaneId = "control-plane-test"
                },
                Failure = new AiRuntimeRecoveryFailureInfo
                {
                    RuntimeFailureIncidentId = runtimeFailureIncidentId,
                    FailedRuntimeInstanceId = failedRuntimeInstanceId,
                    FailedLocalRunId = $"failed-local-{forensicsId}",
                    FailureSignal = "runtime-unhealthy",
                    HealthStatusBefore = "ready",
                    HealthStatusAfter = "unhealthy",
                    SuppressCapacityReason = "runtime-unhealthy",
                    FailureDetectedAtUtc = now
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = "resume-existing-execution",
                    RecoveryKind = "in-flight-execution-resume",
                    Outcome = "completed",
                    Reason = "failed-runtime-instance",
                    RecoveryStartedAtUtc = now,
                    RecoveryCompletedAtUtc = now
                },
                Replacement = new AiRuntimeRecoveryReplacementInfo
                {
                    ReplacementRuntimeInstanceId = replacementRuntimeInstanceId,
                    ReplacementLocalRunId = $"replacement-local-{forensicsId}",
                    DispatchReason = "recovered-shared-run",
                    SelectedAtUtc = now,
                    LocalRunRegisteredAtUtc = now
                },
                Context = new AiRuntimeRecoveryContextInfo
                {
                    SnapshotContextKey = $"snapshot-context-{forensicsId}",
                    RecordContextKey = $"record-context-{forensicsId}",
                    ContextKeyMismatch = true,
                    RehydratedByExecutionId = true,
                    RehydrationReason = "record-context-key-mismatch"
                },
                Dag = new AiRuntimeRecoveryDagInfo
                {
                    StepCount = 100,
                    CompletedStepsBeforeRecovery = 49,
                    RecoveredFromStep = "step-050",
                    FinalCompletedSteps = 100,
                    CompletedStepsReplayed = false,
                    Outcome = "completed"
                },
                Artifacts = new AiRuntimeRecoveryArtifacts
                {
                    Restored =
                    [
                        AiRuntimeRecoveryArtifactName.DurableExecutionId,
                        AiRuntimeRecoveryArtifactName.DagState,
                        AiRuntimeRecoveryArtifactName.CompletedDagSteps,
                        AiRuntimeRecoveryArtifactName.ExecutionContextSnapshot
                    ],
                    Recreated =
                    [
                        AiRuntimeRecoveryArtifactName.ReplacementRuntimeInstance,
                        AiRuntimeRecoveryArtifactName.ReplacementLocalRunId,
                        AiRuntimeRecoveryArtifactName.RuntimeRunExecutionIndexEntry
                    ],
                    LostVolatile =
                    [
                        AiRuntimeRecoveryArtifactName.FailedRuntimeLocalQueueMemory,
                        AiRuntimeRecoveryArtifactName.OldClaimToken,
                        AiRuntimeRecoveryArtifactName.OldLease
                    ]
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        /// <summary>
        /// Creates a recovery forensics event for tests.
        /// </summary>
        /// <param name="eventId">The event identifier.</param>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="eventType">The event type.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="localRunId">The local run identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="timestampUtc">The event timestamp.</param>
        /// <returns>The recovery forensics event.</returns>
        private static AiRuntimeRecoveryForensicsEvent CreateEvent(
            string eventId,
            string forensicsId,
            string eventType,
            string executionId,
            string sharedRunId,
            string localRunId,
            string runtimeInstanceId,
            DateTimeOffset timestampUtc)
        {
            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = eventId,
                ForensicsId = forensicsId,
                TimestampUtc = timestampUtc,
                EventType = eventType,
                Outcome = "ok",
                Reason = "test",
                ExecutionId = executionId,
                SharedRunId = sharedRunId,
                LocalRunId = localRunId,
                RuntimeInstanceId = runtimeInstanceId
            };
        }

        /// <summary>
        /// Provides isolated MongoDB test state for recovery forensics tests.
        /// </summary>
        private sealed class MongoForensicsTestFixture : IAsyncDisposable
        {
            private readonly IMongoDatabase _database;
            private readonly string _collectionName;
            /// <summary>
            /// Gets the isolated MongoDB database name.
            /// </summary>
            public string DatabaseName =>
                _database.DatabaseNamespace.DatabaseName;

            /// <summary>
            /// Initializes a new instance of the <see cref="MongoForensicsTestFixture"/> class.
            /// </summary>
            /// <param name="database">The MongoDB database.</param>
            /// <param name="collectionName">The isolated collection name.</param>
            public MongoForensicsTestFixture(IMongoDatabase database, string collectionName)
            {
                _database = database;
                _collectionName = collectionName;
                Collection = database.GetCollection<AiRuntimeRecoveryForensicsRecord>(collectionName);
            }

            /// <summary>
            /// Gets the isolated MongoDB collection.
            /// </summary>
            public IMongoCollection<AiRuntimeRecoveryForensicsRecord> Collection { get; }

            /// <summary>
            /// Creates the MongoDB recovery forensics store.
            /// </summary>
            /// <param name="database">
            /// The optional MongoDB database instance. When omitted, the fixture database is used.
            /// </param>
            /// <returns>The MongoDB recovery forensics store.</returns>
            public MongoAiRuntimeRecoveryForensicsStore CreateStore(
                IMongoDatabase? database = null)
            {
                return new MongoAiRuntimeRecoveryForensicsStore(
                    database ?? _database,
                    Options.Create(
                        new AiRuntimeRecoveryForensicsMongoOptions
                        {
                            CollectionName = _collectionName,
                            EnsureIndexes = true
                        }));
            }

            /// <inheritdoc />
            public async ValueTask DisposeAsync()
            {
                await _database.DropCollectionAsync(_collectionName);
            }
        }
    }
}