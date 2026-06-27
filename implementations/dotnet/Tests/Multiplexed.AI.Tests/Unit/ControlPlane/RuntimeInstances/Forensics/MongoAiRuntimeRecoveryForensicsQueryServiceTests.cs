using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Tests the MongoDB-backed runtime recovery forensics read model query service.
    /// </summary>
    public sealed class MongoAiRuntimeRecoveryForensicsQueryServiceTests : IAsyncLifetime
    {
        private const string FullTimelineForensicsId = "runtime-recovery:execution-1:shared-run-1:local-run-failed-1";

        private readonly MongoClient _client;
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<AiRuntimeRecoveryForensicsRecord> _collection;
        private readonly MongoAiRuntimeRecoveryForensicsQueryService _service;
        private readonly string _databaseName;

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoAiRuntimeRecoveryForensicsQueryServiceTests"/> class.
        /// </summary>
        public MongoAiRuntimeRecoveryForensicsQueryServiceTests()
        {
            var connectionString = Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION_STRING")
                ?? Environment.GetEnvironmentVariable("MONGODB_TEST_CONNECTION_STRING")
                ?? "mongodb://localhost:27017";

            _databaseName = $"multiplexed_forensics_{Guid.NewGuid():N}";
            _client = new MongoClient(connectionString);
            _database = _client.GetDatabase(_databaseName);

            var options = Options.Create(
                new AiRuntimeRecoveryForensicsMongoOptions
                {
                    CollectionName = "recovery_forensics_query_tests",
                    EnsureIndexes = false
                });

            _collection = _database.GetCollection<AiRuntimeRecoveryForensicsRecord>(options.Value.CollectionName);
            _service = new MongoAiRuntimeRecoveryForensicsQueryService(
                _database,
                options);
        }

        /// <summary>
        /// Cleans the MongoDB test database before each test.
        /// </summary>
        /// <returns>A completed task.</returns>
        public async Task InitializeAsync()
        {
            await _collection.DeleteManyAsync(
                    Builders<AiRuntimeRecoveryForensicsRecord>.Filter.Empty)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Drops the MongoDB test database after each test.
        /// </summary>
        /// <returns>A completed task.</returns>
        public async Task DisposeAsync()
        {
            await _client.DropDatabaseAsync(_databaseName).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that a recovery forensics record can be read by forensics id and projected into an ordered timeline.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task GetByForensicsIdAsync_Should_Return_ReadModel_With_Ordered_Timeline()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);

            var model = await _service
                .GetByForensicsIdAsync(FullTimelineForensicsId)
                .ConfigureAwait(false);

            Assert.NotNull(model);
            Assert.Equal(FullTimelineForensicsId, model.ForensicsId);
            Assert.Equal("execution-1", model.ExecutionId);
            Assert.Equal("shared-run-1", model.SharedRunId);
            Assert.Equal("tenant-1", model.TenantId);
            Assert.Equal("control-plane-1", model.ControlPlaneId);

            Assert.Equal(
                new[]
                {
                    "execution.recovery.candidate.detected",
                    "shared.run.requeued.for.resume",
                    "failed.local.run.marked.requeued.for.recovery",
                    "replacement.runtime.selected",
                    "replacement.local.run.registered",
                    "resume.context.seeded",
                    "dag.resume.started",
                    "dag.resume.completed",
                    "execution.recovery.completed"
                },
                model.Timeline.Select(x => x.EventType).ToArray());

            var completed = model.Timeline[^1];

            Assert.Equal("completed", completed.Outcome);
            Assert.Equal("runtime-replacement-1", completed.RuntimeInstanceId);
            Assert.Equal("local-run-replacement-1", completed.LocalRunId);
            Assert.Equal("ctx-tenant-1", completed.Metadata["resume.contextKey"]);
        }

        /// <summary>
        /// Verifies that search can filter records by execution id.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SearchAsync_Should_Filter_By_ExecutionId()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);
            await InsertAsync(CreateRecord("runtime-recovery:execution-2:shared-run-2:local-run-2", "execution-2", "shared-run-2", "tenant-1", "runtime-other", "runtime-next", "runtime-failure:runtime-other", false)).ConfigureAwait(false);

            var result = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        ExecutionId = "execution-1",
                        Limit = 20
                    })
                .ConfigureAwait(false);

            Assert.Equal(1, result.Count);
            Assert.Equal("execution-1", result.Items.Single().ExecutionId);
            Assert.Equal(20, result.Limit);
        }

        /// <summary>
        /// Verifies that search can filter records by shared run id.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SearchAsync_Should_Filter_By_SharedRunId()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);
            await InsertAsync(CreateRecord("runtime-recovery:execution-2:shared-run-2:local-run-2", "execution-2", "shared-run-2", "tenant-1", "runtime-other", "runtime-next", "runtime-failure:runtime-other", false)).ConfigureAwait(false);

            var result = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        SharedRunId = "shared-run-1"
                    })
                .ConfigureAwait(false);

            Assert.Equal(1, result.Count);
            Assert.Equal("shared-run-1", result.Items.Single().SharedRunId);
        }

        /// <summary>
        /// Verifies that search can filter records by failed or replacement runtime instance id.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SearchAsync_Should_Filter_By_RuntimeInstanceId()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);
            await InsertAsync(CreateRecord("runtime-recovery:execution-2:shared-run-2:local-run-2", "execution-2", "shared-run-2", "tenant-1", "runtime-other", "runtime-next", "runtime-failure:runtime-other", false)).ConfigureAwait(false);

            var failedRuntimeResult = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        RuntimeInstanceId = "runtime-failed-1"
                    })
                .ConfigureAwait(false);

            var replacementRuntimeResult = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        RuntimeInstanceId = "runtime-replacement-1"
                    })
                .ConfigureAwait(false);

            Assert.Equal(1, failedRuntimeResult.Count);
            Assert.Equal(FullTimelineForensicsId, failedRuntimeResult.Items.Single().ForensicsId);

            Assert.Equal(1, replacementRuntimeResult.Count);
            Assert.Equal(FullTimelineForensicsId, replacementRuntimeResult.Items.Single().ForensicsId);
        }

        /// <summary>
        /// Verifies that search can filter records by event type inside the timeline.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SearchAsync_Should_Filter_By_EventType()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);
            await InsertAsync(CreateRecord("runtime-recovery:execution-2:shared-run-2:local-run-2", "execution-2", "shared-run-2", "tenant-1", "runtime-other", "runtime-next", "runtime-failure:runtime-other", false)).ConfigureAwait(false);

            var result = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        EventType = "dag.resume.completed"
                    })
                .ConfigureAwait(false);

            Assert.Equal(1, result.Count);
            Assert.Equal(FullTimelineForensicsId, result.Items.Single().ForensicsId);
            Assert.Contains(
                result.Items.Single().Timeline,
                item => string.Equals(item.EventType, "dag.resume.completed", StringComparison.Ordinal));
        }

        /// <summary>
        /// Verifies that search can return only recent failed recovery records.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SearchAsync_Should_Return_Recent_Failures()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);
            await InsertAsync(CreateRecord("runtime-recovery:execution-failed:shared-run-failed:local-run-failed", "execution-failed", "shared-run-failed", "tenant-1", "runtime-failed-2", "runtime-replacement-2", "runtime-failure:runtime-failed-2", true)).ConfigureAwait(false);

            var result = await _service
                .SearchAsync(
                    new AiRuntimeRecoveryForensicsQuery
                    {
                        RecentFailuresOnly = true
                    })
                .ConfigureAwait(false);

            Assert.Equal(1, result.Count);
            Assert.Equal("execution-failed", result.Items.Single().ExecutionId);
            Assert.Contains(
                result.Items.Single().Timeline,
                item => string.Equals(item.EventType, "execution.recovery.failed", StringComparison.Ordinal));
        }

        /// <summary>
        /// Verifies that GetTimelineAsync returns only the ordered timeline for a recovery forensics id.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task GetTimelineAsync_Should_Return_Ordered_Timeline()
        {
            await InsertAsync(CreateFullRecoveryRecord()).ConfigureAwait(false);

            var timeline = await _service
                .GetTimelineAsync(FullTimelineForensicsId)
                .ConfigureAwait(false);

            Assert.Equal(9, timeline.Count);
            Assert.Equal("execution.recovery.candidate.detected", timeline[0].EventType);
            Assert.Equal("execution.recovery.completed", timeline[^1].EventType);
        }

        /// <summary>
        /// Inserts one recovery forensics record into MongoDB.
        /// </summary>
        /// <param name="record">The record to insert.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task InsertAsync(
            AiRuntimeRecoveryForensicsRecord record)
        {
            await _collection.InsertOneAsync(record).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a complete successful recovery timeline record.
        /// </summary>
        /// <returns>The recovery forensics record.</returns>
        private static AiRuntimeRecoveryForensicsRecord CreateFullRecoveryRecord()
        {
            var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);

            var record = CreateRecord(
                FullTimelineForensicsId,
                "execution-1",
                "shared-run-1",
                "tenant-1",
                "runtime-failed-1",
                "runtime-replacement-1",
                "runtime-failure:runtime-failed-1",
                false);

            return record with
            {
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc.AddMinutes(5),
                Events = CreateSuccessfulTimeline(createdAtUtc)
                    .OrderByDescending(x => x.TimestampUtc)
                    .ToList()
            };
        }

        /// <summary>
        /// Creates a recovery forensics record.
        /// </summary>
        /// <param name="forensicsId">The forensics id.</param>
        /// <param name="executionId">The execution id.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance id.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance id.</param>
        /// <param name="runtimeFailureIncidentId">The runtime failure incident id.</param>
        /// <param name="failed">Whether to create a failed recovery event.</param>
        /// <returns>The recovery forensics record.</returns>
        private static AiRuntimeRecoveryForensicsRecord CreateRecord(
            string forensicsId,
            string executionId,
            string sharedRunId,
            string tenantId,
            string failedRuntimeInstanceId,
            string replacementRuntimeInstanceId,
            string runtimeFailureIncidentId,
            bool failed)
        {
            var createdAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);

            return new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = executionId,
                    SharedRunId = sharedRunId,
                    TenantId = tenantId,
                    ControlPlaneId = "control-plane-1"
                },
                Failure = new AiRuntimeRecoveryFailureInfo
                {
                    RuntimeFailureIncidentId = runtimeFailureIncidentId,
                    FailedRuntimeInstanceId = failedRuntimeInstanceId,
                    FailedLocalRunId = "local-run-failed-1",
                    FailureSignal = "runtime-unhealthy",
                    SuppressCapacityReason = "runtime instance became unhealthy",
                    FailureDetectedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
                },
                Replacement = new AiRuntimeRecoveryReplacementInfo
                {
                    ReplacementRuntimeInstanceId = replacementRuntimeInstanceId,
                    ReplacementLocalRunId = "local-run-replacement-1"
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = "resume-existing-execution",
                    Outcome = failed ? "failed" : "completed"
                },
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc.AddMinutes(1),
                Events = failed
                    ? CreateFailedTimeline(forensicsId, executionId, sharedRunId, failedRuntimeInstanceId, replacementRuntimeInstanceId, createdAtUtc)
                    : new[]
                    {
                        CreateEvent(
                            forensicsId,
                            executionId,
                            sharedRunId,
                            "execution.recovery.candidate.detected",
                            "detected",
                            failedRuntimeInstanceId,
                            "local-run-failed-1",
                            createdAtUtc)
                    }
            };
        }

        /// <summary>
        /// Creates a successful recovery timeline.
        /// </summary>
        /// <param name="startedAtUtc">The timeline start timestamp.</param>
        /// <returns>The ordered successful timeline.</returns>
        private static IReadOnlyList<AiRuntimeRecoveryForensicsEvent> CreateSuccessfulTimeline(
            DateTimeOffset startedAtUtc)
        {
            return new[]
            {
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "execution.recovery.candidate.detected", "detected", "runtime-failed-1", "local-run-failed-1", startedAtUtc.AddSeconds(1)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "shared.run.requeued.for.resume", "requeued", "runtime-failed-1", "local-run-failed-1", startedAtUtc.AddSeconds(2)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "failed.local.run.marked.requeued.for.recovery", "requeued", "runtime-failed-1", "local-run-failed-1", startedAtUtc.AddSeconds(3)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "replacement.runtime.selected", "selected", "runtime-replacement-1", "local-run-failed-1", startedAtUtc.AddSeconds(4)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "replacement.local.run.registered", "registered", "runtime-replacement-1", "local-run-replacement-1", startedAtUtc.AddSeconds(5)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "resume.context.seeded", "seeded", "runtime-replacement-1", "local-run-replacement-1", startedAtUtc.AddSeconds(6)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "dag.resume.started", "started", "runtime-replacement-1", "local-run-replacement-1", startedAtUtc.AddSeconds(7)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "dag.resume.completed", "completed", "runtime-replacement-1", "local-run-replacement-1", startedAtUtc.AddSeconds(8)),
                CreateEvent(FullTimelineForensicsId, "execution-1", "shared-run-1", "execution.recovery.completed", "completed", "runtime-replacement-1", "local-run-replacement-1", startedAtUtc.AddSeconds(9))
            };
        }

        /// <summary>
        /// Creates a failed recovery timeline.
        /// </summary>
        /// <param name="forensicsId">The forensics id.</param>
        /// <param name="executionId">The execution id.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance id.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance id.</param>
        /// <param name="startedAtUtc">The timeline start timestamp.</param>
        /// <returns>The failed timeline.</returns>
        private static IReadOnlyList<AiRuntimeRecoveryForensicsEvent> CreateFailedTimeline(
            string forensicsId,
            string executionId,
            string sharedRunId,
            string failedRuntimeInstanceId,
            string replacementRuntimeInstanceId,
            DateTimeOffset startedAtUtc)
        {
            return new[]
            {
                CreateEvent(forensicsId, executionId, sharedRunId, "execution.recovery.candidate.detected", "detected", failedRuntimeInstanceId, "local-run-failed-1", startedAtUtc.AddSeconds(1)),
                CreateEvent(forensicsId, executionId, sharedRunId, "replacement.runtime.selected", "selected", replacementRuntimeInstanceId, "local-run-replacement-1", startedAtUtc.AddSeconds(2)),
                CreateEvent(forensicsId, executionId, sharedRunId, "execution.recovery.failed", "failed", replacementRuntimeInstanceId, "local-run-replacement-1", startedAtUtc.AddSeconds(3))
            };
        }

        /// <summary>
        /// Creates a recovery forensics event.
        /// </summary>
        /// <param name="forensicsId">The forensics id.</param>
        /// <param name="executionId">The execution id.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="eventType">The event type.</param>
        /// <param name="outcome">The event outcome.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="localRunId">The local run id.</param>
        /// <param name="timestampUtc">The timestamp.</param>
        /// <returns>The event.</returns>
        private static AiRuntimeRecoveryForensicsEvent CreateEvent(
            string forensicsId,
            string executionId,
            string sharedRunId,
            string eventType,
            string outcome,
            string runtimeInstanceId,
            string localRunId,
            DateTimeOffset timestampUtc)
        {
            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = $"{forensicsId}:{eventType}:{timestampUtc.ToUnixTimeMilliseconds()}",
                ForensicsId = forensicsId,
                TimestampUtc = timestampUtc,
                EventType = eventType,
                Outcome = outcome,
                Reason = eventType,
                ExecutionId = executionId,
                SharedRunId = sharedRunId,
                LocalRunId = localRunId,
                RuntimeInstanceId = runtimeInstanceId,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["tenant.id"] = "tenant-1",
                    ["replacement.runtimeInstanceId"] = "runtime-replacement-1",
                    ["replacement.localRunId"] = "local-run-replacement-1",
                    ["failed.runtimeInstanceId"] = "runtime-failed-1",
                    ["failed.localRunId"] = "local-run-failed-1",
                    ["resume.contextKey"] = "ctx-tenant-1"
                }
            };
        }
    }
}
