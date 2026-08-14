using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    public sealed class RedisAiSharedRunStoreTests : IAsyncLifetime
    {
        private readonly string _keyPrefix =
            $"test:ai:shared-runs:{Guid.NewGuid():N}";

        private readonly string _controlPlaneId =
            $"test-control-plane-{Guid.NewGuid():N}";

        private readonly string _runIdPrefix =
            $"test-shared-run-{Guid.NewGuid():N}";

        private IConnectionMultiplexer? _connection;

        public async Task InitializeAsync()
        {
            _connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        }

        public async Task DisposeAsync()
        {
            if (_connection is null)
            {
                return;
            }

            var database = _connection.GetDatabase();

            var server = _connection
                .GetServer(_connection.GetEndPoints().First());

            var keys = server
                .Keys(
                    database: database.Database,
                    pattern: $"{_keyPrefix}*")
                .Concat(
                    server.Keys(
                        database: database.Database,
                        pattern: $"*control-plane:{_controlPlaneId}*"))
                .Concat(
                    server.Keys(
                        database: database.Database,
                        pattern: $"*{_runIdPrefix}*"))
                .Distinct()
                .ToArray();

            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }

            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        [Fact]
        public async Task CreateAsync_Should_Create_Record()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-1");

            var record = CreateRecord(
                sharedRunId,
                AiSharedRunStatus.AssignedToInstance);

            var created = await store.CreateAsync(record);

            Assert.Equal(sharedRunId, created.SharedRunId);
            Assert.Equal(_controlPlaneId, created.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, created.Status);

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(sharedRunId, loaded!.SharedRunId);
            Assert.Equal(_controlPlaneId, loaded.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, loaded.Status);
            Assert.Equal("pipeline-1", loaded.RunRequest.PipelineName);
        }

        [Fact]
        public async Task CreateAsync_Should_Preserve_Typed_Placement()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-placement");

            var placement =
                new AiRunPlacementDirective
                {
                    Target = new AiRunPlacementTarget
                    {
                        RuntimeInstanceId = "runtime-target"
                    },
                    Requirement = AiRunPlacementRequirement.Required,
                    Fallback = AiRunPlacementFallback.Reject
                };

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally,
                    placement: placement));

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.NotNull(loaded!.Placement);
            Assert.Equal(
                "runtime-target",
                loaded.Placement!.Target.RuntimeInstanceId);
            Assert.Equal(
                AiRunPlacementRequirement.Required,
                loaded.Placement.Requirement);
            Assert.Equal(
                AiRunPlacementFallback.Reject,
                loaded.Placement.Fallback);
        }

        [Fact]
        public async Task CreateAsync_Should_Reject_Duplicate_Atomically()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-1");

            var record = CreateRecord(
                sharedRunId,
                AiSharedRunStatus.AssignedToInstance);

            await store.CreateAsync(record);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.CreateAsync(record));
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Record_Is_Missing()
        {
            var store = CreateStore();

            var loaded = await store.GetAsync(
                RunId("missing-run"));

            Assert.Null(loaded);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Records_From_ZSet_Index()
        {
            var store = CreateStore();

            var sharedRunB =
                RunId("shared-run-b");

            var sharedRunA =
                RunId("shared-run-a");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunB,
                    AiSharedRunStatus.AssignedToInstance,
                    DateTimeOffset.UtcNow.AddMinutes(1)));

            await store.CreateAsync(
                CreateRecord(
                    sharedRunA,
                    AiSharedRunStatus.AssignedToInstance,
                    DateTimeOffset.UtcNow));

            var records = await store.ListAsync();

            Assert.Equal(2, records.Count);
            Assert.Equal(sharedRunA, records[0].SharedRunId);
            Assert.Equal(sharedRunB, records[1].SharedRunId);

            Assert.All(records, record =>
            {
                Assert.Equal(_controlPlaneId, record.ControlPlaneId);
            });
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Cancelled_By_Default()
        {
            var store = CreateStore();

            var activeRun =
                RunId("shared-run-1");

            var cancelledRun =
                RunId("shared-run-2");

            await store.CreateAsync(
                CreateRecord(activeRun, AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord(cancelledRun, AiSharedRunStatus.Cancelled));

            var records = await store.ListAsync();

            Assert.Single(records);
            Assert.Equal(activeRun, records[0].SharedRunId);
            Assert.Equal(_controlPlaneId, records[0].ControlPlaneId);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Cancelled_When_Requested()
        {
            var store = CreateStore();

            var activeRun =
                RunId("shared-run-1");

            var cancelledRun =
                RunId("shared-run-2");

            await store.CreateAsync(
                CreateRecord(activeRun, AiSharedRunStatus.AssignedToInstance));

            await store.CreateAsync(
                CreateRecord(cancelledRun, AiSharedRunStatus.Cancelled));

            var records = await store.ListAsync(includeCancelled: true);

            Assert.Equal(2, records.Count);

            Assert.All(records, record =>
            {
                Assert.Equal(_controlPlaneId, record.ControlPlaneId);
            });
        }

        [Fact]
        public async Task CancelAsync_Should_Cancel_NonTerminal_Record_Atomically()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-1");

            await store.CreateAsync(
                CreateRecord(sharedRunId, AiSharedRunStatus.QueuedGlobally));

            var cancelled = await store.CancelAsync(
                sharedRunId,
                reason: "operator cancel",
                requestedBy: "tester",
                source: "unit-test");

            Assert.NotNull(cancelled);
            Assert.Equal(_controlPlaneId, cancelled!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Cancelled, cancelled.Status);
            Assert.Equal("operator cancel", cancelled.Reason);
            Assert.Equal("operator cancel", cancelled.FailureReason);
            Assert.Equal("tester", cancelled.RequestedBy);
            Assert.Equal("unit-test", cancelled.Source);

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(_controlPlaneId, loaded!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Cancelled, loaded.Status);
        }

        [Theory]
        [InlineData(AiSharedRunStatus.Completed)]
        [InlineData(AiSharedRunStatus.Failed)]
        [InlineData(AiSharedRunStatus.Cancelled)]
        public async Task CancelAsync_Should_Return_Terminal_Record_Without_Changing_Status(
            AiSharedRunStatus terminalStatus)
        {
            var store = CreateStore();

            var sharedRunId =
                RunId($"shared-run-{terminalStatus}");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    terminalStatus,
                    failureReason: "existing failure"));

            var result = await store.CancelAsync(
                sharedRunId,
                reason: "new cancel",
                requestedBy: "tester");

            Assert.NotNull(result);
            Assert.Equal(_controlPlaneId, result!.ControlPlaneId);
            Assert.Equal(terminalStatus, result.Status);
            Assert.Equal("existing failure", result.FailureReason);
        }

        [Fact]
        public async Task CancelAsync_Should_Return_Null_When_Record_Is_Missing()
        {
            var store = CreateStore();

            var cancelled = await store.CancelAsync(
                RunId("missing-run"));

            Assert.Null(cancelled);
        }

        [Fact]
        public async Task CreateAsync_Should_Preserve_Metadata()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-1");

            var record = CreateRecord(
                sharedRunId,
                AiSharedRunStatus.AssignedToInstance,
                metadata: new Dictionary<string, string>
                {
                    ["tenant"] = "tenant-1",
                    ["priority"] = "high"
                });

            await store.CreateAsync(record);

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(_controlPlaneId, loaded!.ControlPlaneId);
            Assert.Equal(_controlPlaneId, loaded.Metadata["controlPlaneId"]);
            Assert.Equal("tenant-1", loaded.Metadata["tenant"]);
            Assert.Equal("high", loaded.Metadata["priority"]);
        }

        [Fact]
        public async Task CreateAsync_Should_Allow_Only_One_Concurrent_Create_For_Same_SharedRunId()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-concurrent");

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                    Task.Run(async () =>
                    {
                        try
                        {
                            var created = await store.CreateAsync(
                                CreateRecord(
                                    sharedRunId,
                                    AiSharedRunStatus.AssignedToInstance,
                                    metadata: new Dictionary<string, string>
                                    {
                                        ["attempt"] = index.ToString()
                                    }));

                            return (Success: true, Record: created, Exception: (Exception?)null);
                        }
                        catch (Exception exception)
                        {
                            return (Success: false, Record: (AiSharedRunRecord?)null, Exception: exception);
                        }
                    }))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            var successful = results
                .Where(result => result.Success)
                .ToArray();

            var failed = results
                .Where(result => !result.Success)
                .ToArray();

            Assert.Single(successful);
            Assert.Equal(_controlPlaneId, successful[0].Record!.ControlPlaneId);
            Assert.Equal(19, failed.Length);

            Assert.All(failed, result =>
            {
                Assert.IsType<InvalidOperationException>(result.Exception);
                Assert.Contains("already exists", result.Exception!.Message);
            });

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(sharedRunId, loaded!.SharedRunId);
            Assert.Equal(_controlPlaneId, loaded.ControlPlaneId);
        }

        [Fact]
        public async Task CancelAsync_Should_Be_Safe_When_Called_Concurrently()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-cancel-concurrent");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally));

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                    Task.Run(() =>
                        store.CancelAsync(
                            sharedRunId,
                            reason: $"cancel-{index}",
                            requestedBy: $"tester-{index}",
                            source: "unit-test")))
                .ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, result =>
            {
                Assert.NotNull(result);
                Assert.Equal(_controlPlaneId, result!.ControlPlaneId);
                Assert.Equal(AiSharedRunStatus.Cancelled, result.Status);
            });

            var loaded = await store.GetAsync(sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(_controlPlaneId, loaded!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Cancelled, loaded.Status);
            Assert.False(string.IsNullOrWhiteSpace(loaded.FailureReason));
            Assert.StartsWith("cancel-", loaded.FailureReason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Update_NonTerminal_Run()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-1");

            await store.CreateAsync(
                CreateRecord(sharedRunId, AiSharedRunStatus.AssignedToInstance));

            var updated = await store.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId: "runtime-1",
                localRunId: "local-run-1",
                executionId: "execution-1",
                reason: "dispatch succeeded");

            Assert.NotNull(updated);
            Assert.Equal(_controlPlaneId, updated!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Dispatched, updated.Status);
            Assert.Equal("runtime-1", updated.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", updated.LocalRunId);
            Assert.Equal("execution-1", updated.ExecutionId);
            Assert.Equal("dispatch succeeded", updated.Reason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Preserve_First_Durable_Ownership()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-first-dispatch-wins");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    AiSharedRunStatus.AssignedToInstance));

            await store.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId: "runtime-first",
                localRunId: "local-first",
                executionId: "execution-first",
                reason: "first dispatch");

            var second =
                await store.MarkDispatchedAsync(
                    sharedRunId,
                    runtimeInstanceId: "runtime-second",
                    localRunId: "local-second",
                    executionId: "execution-second",
                    reason: "delayed duplicate dispatch");

            Assert.NotNull(second);
            Assert.Equal(_controlPlaneId, second!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Dispatched, second.Status);
            Assert.Equal(
                "runtime-first",
                second.AssignedRuntimeInstanceId);
            Assert.Equal("local-first", second.LocalRunId);
            Assert.Equal("execution-first", second.ExecutionId);
            Assert.Equal("first dispatch", second.Reason);
        }

        [Fact]
        public async Task MarkDispatchedAsync_Should_Return_Null_When_Run_Is_Unknown()
        {
            var store = CreateStore();

            var updated = await store.MarkDispatchedAsync(
                RunId("missing-run"),
                runtimeInstanceId: "runtime-1");

            Assert.Null(updated);
        }

        [Fact]
        public async Task MarkDispatchFailedAsync_Should_Persist_Failure_Metadata_Without_Dispatching_Run()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-dispatch-failed");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally));

            var updated = await store.MarkDispatchFailedAsync(
                sharedRunId,
                runtimeInstanceId: "runtime-1",
                failureReason: "http-circuit-open",
                message: "HTTP runtime circuit breaker is open.");

            Assert.NotNull(updated);
            Assert.Equal(_controlPlaneId, updated!.ControlPlaneId);

            Assert.Equal(
                AiSharedRunStatus.QueuedGlobally,
                updated.Status);

            Assert.Null(
                updated.AssignedRuntimeInstanceId);

            Assert.Equal(
                "http-circuit-open",
                updated.FailureReason);

            Assert.Equal(
                "HTTP runtime circuit breaker is open.",
                updated.Reason);

            Assert.Null(updated.LocalRunId);
            Assert.Null(updated.ExecutionId);

            var loaded =
                await store.GetAsync(
                    sharedRunId);

            Assert.NotNull(loaded);
            Assert.Equal(_controlPlaneId, loaded!.ControlPlaneId);

            Assert.Equal(
                AiSharedRunStatus.QueuedGlobally,
                loaded.Status);

            Assert.Null(
                loaded.AssignedRuntimeInstanceId);

            Assert.Equal(
                "http-circuit-open",
                loaded.FailureReason);

            Assert.Equal(
                "HTTP runtime circuit breaker is open.",
                loaded.Reason);

            Assert.Null(loaded.LocalRunId);
            Assert.Null(loaded.ExecutionId);
        }

        /// <summary>
        /// Verifies that dispatch ownership metadata is durably persisted in Redis.
        /// </summary>
        [Fact]
        public async Task MarkDispatchedAsync_Should_Persist_Dispatch_Ownership_Metadata()
        {
            var store = CreateStore();

            var sharedRunId =
                RunId("shared-run-dispatch-ownership");

            await store.CreateAsync(
                CreateRecord(sharedRunId, AiSharedRunStatus.AssignedToInstance));

            await store.MarkDispatchedAsync(
                sharedRunId,
                runtimeInstanceId: "runtime-1",
                localRunId: "local-run-1",
                executionId: "execution-1",
                reason: "dispatch succeeded");

            var loaded =
                await store.GetAsync(sharedRunId);

            var records =
                await store.ListAsync();

            Assert.NotNull(loaded);
            Assert.Equal(_controlPlaneId, loaded!.ControlPlaneId);
            Assert.Equal(AiSharedRunStatus.Dispatched, loaded.Status);
            Assert.Equal("runtime-1", loaded.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", loaded.LocalRunId);
            Assert.Equal("execution-1", loaded.ExecutionId);
            Assert.Equal("dispatch succeeded", loaded.Reason);

            Assert.Contains(
                records,
                record =>
                    record.SharedRunId == sharedRunId &&
                    record.Status == AiSharedRunStatus.Dispatched &&
                    record.AssignedRuntimeInstanceId == "runtime-1" &&
                    record.LocalRunId == "local-run-1" &&
                    record.ExecutionId == "execution-1");
        }

        private RedisAiSharedRunStore CreateStore()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedRunStore(
                _connection,
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = _keyPrefix,
                    ListScanLimit = 100
                }),
                new StaticAiControlPlaneIdResolver(_controlPlaneId));
        }

        [Theory]
        [InlineData(AiSharedRunStatus.Completed)]
        [InlineData(AiSharedRunStatus.Failed)]
        [InlineData(AiSharedRunStatus.Cancelled)]
        public async Task MarkDispatchFailedAsync_Should_Return_Terminal_Record_Without_Changing_Status(
    AiSharedRunStatus terminalStatus)
        {
            var store = CreateStore();

            var sharedRunId =
                RunId($"shared-run-dispatch-failed-{terminalStatus}");

            await store.CreateAsync(
                CreateRecord(
                    sharedRunId,
                    terminalStatus,
                    failureReason: "existing terminal failure"));

            var updated = await store.MarkDispatchFailedAsync(
                sharedRunId,
                runtimeInstanceId: "runtime-1",
                failureReason: "http-circuit-open",
                message: "HTTP runtime circuit breaker is open.");

            Assert.NotNull(updated);
            Assert.Equal(_controlPlaneId, updated!.ControlPlaneId);

            Assert.Equal(
                terminalStatus,
                updated.Status);

            Assert.Equal(
                "existing terminal failure",
                updated.FailureReason);

            Assert.Null(updated.AssignedRuntimeInstanceId);
            Assert.Null(updated.LocalRunId);
            Assert.Null(updated.ExecutionId);

            var loaded =
                await store.GetAsync(
                    sharedRunId);

            Assert.NotNull(loaded);

            Assert.Equal(
                terminalStatus,
                loaded!.Status);

            Assert.Equal(
                "existing terminal failure",
                loaded.FailureReason);

            Assert.Null(loaded.AssignedRuntimeInstanceId);
            Assert.Null(loaded.LocalRunId);
            Assert.Null(loaded.ExecutionId);
        }

        private AiSharedRunRecord CreateRecord(
            string sharedRunId,
            AiSharedRunStatus status,
            DateTimeOffset? submittedAtUtc = null,
            string? failureReason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            AiRunPlacementDirective? placement = null)
        {
            var now = submittedAtUtc ?? DateTimeOffset.UtcNow;

            var effectiveMetadata =
                new Dictionary<string, string>(
                    metadata ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal)
                {
                    ["controlPlaneId"] = _controlPlaneId
                };

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = _controlPlaneId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                },
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: "tenant-1"),
                Placement = placement,
                FailureReason = failureReason,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = effectiveMetadata
            };
        }

        private string RunId(
            string name)
        {
            return $"{_runIdPrefix}-{name}";
        }
    }
}