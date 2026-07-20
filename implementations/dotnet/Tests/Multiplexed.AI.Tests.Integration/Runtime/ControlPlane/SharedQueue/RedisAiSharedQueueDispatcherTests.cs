using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue.Redis;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;

namespace Multiplexed.AI.Tests.Integration.Runtime.ControlPlane.SharedQueue
{
    public sealed class RedisAiSharedQueueDispatcherTests : IAsyncLifetime
    {
        private readonly string _runKeyPrefix =
            $"test:ai:shared-runs:{Guid.NewGuid():N}";

        private readonly string _queueKeyPrefix =
            $"test:ai:shared-queue:{Guid.NewGuid():N}";

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

            var server = _connection.GetServer(
                _connection.GetEndPoints().First());

            var keys = server
                .Keys(
                    database: database.Database,
                    pattern: $"{_runKeyPrefix}*")
                .Concat(
                    server.Keys(
                        database: database.Database,
                        pattern: $"{_queueKeyPrefix}*"))
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
        public async Task DispatchNextAsync_Should_Dispatch_Redis_Queued_Run_And_Update_Redis_State()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRunId =
                RunId("shared-run-1");

            await store.CreateAsync(
                CreateSharedRun(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(sharedRunId));

            var runDispatcher = new FakeSharedRunDispatcher();
            var admissionController = new FakeRunAdmissionController();
            var runtimeInstanceRegistry = await CreateReadyRuntimeRegistryAsync();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                admissionController,
                new InMemoryAiRuntimeAdmissionReservationStore(),
                runtimeInstanceRegistry,
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver(_controlPlaneId),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "redis-integration-test",
                Reason = "runtime instance has capacity",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = _controlPlaneId
                }
            });

            Assert.True(result.Success);
            Assert.False(result.NoItemAvailable);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);

            Assert.NotNull(result.QueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, result.QueueItem!.Status);
            Assert.Equal(_controlPlaneId, result.QueueItem.ControlPlaneId);

            Assert.NotNull(result.SharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.SharedRun!.Status);
            Assert.Equal(_controlPlaneId, result.SharedRun.ControlPlaneId);
            Assert.Equal("runtime-1", result.SharedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", result.SharedRun.LocalRunId);
            Assert.Equal("execution-1", result.SharedRun.ExecutionId);

            var loadedQueueItem = await queue.GetAsync(sharedRunId);

            Assert.NotNull(loadedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loadedQueueItem!.Status);
            Assert.Equal(_controlPlaneId, loadedQueueItem.ControlPlaneId);
            Assert.Equal("runtime-1", loadedQueueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", loadedQueueItem.ClaimedByWorkerId);
            Assert.False(string.IsNullOrWhiteSpace(loadedQueueItem.ClaimToken));

            var loadedRun = await store.GetAsync(sharedRunId);

            Assert.NotNull(loadedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, loadedRun!.Status);
            Assert.Equal(_controlPlaneId, loadedRun.ControlPlaneId);
            Assert.Equal("runtime-1", loadedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", loadedRun.LocalRunId);
            Assert.Equal("execution-1", loadedRun.ExecutionId);

            Assert.NotNull(runDispatcher.LastRequest);
            Assert.Equal(sharedRunId, runDispatcher.LastRequest!.SharedRun.SharedRunId);
            Assert.Equal(_controlPlaneId, runDispatcher.LastRequest.SharedRun.ControlPlaneId);
            Assert.Equal("runtime-1", runDispatcher.LastRequest.RuntimeInstanceId);
            Assert.Equal("correlation-1", runDispatcher.LastRequest.CorrelationId);
            Assert.Equal("tester", runDispatcher.LastRequest.RequestedBy);
            Assert.Equal("redis-integration-test", runDispatcher.LastRequest.Source);
            Assert.Equal(_controlPlaneId, runDispatcher.LastRequest.Metadata["controlPlaneId"]);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Return_NoItemAvailable_When_Redis_Queue_Is_Empty()
        {
            var dispatcher = new AiSharedQueueDispatcher(
                CreateQueue(),
                CreateRunStore(),
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                new InMemoryAiRuntimeInstanceRegistry(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver(_controlPlaneId),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = _controlPlaneId
                }
            });

            Assert.False(result.Success);
            Assert.True(result.NoItemAvailable);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_Redis_Item_When_Shared_Run_Is_Missing()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRunId =
                RunId("shared-run-1");

            await queue.EnqueueAsync(
                CreateQueueItem(sharedRunId));

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                new InMemoryAiRuntimeInstanceRegistry(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver(_controlPlaneId),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = _controlPlaneId
                }
            });

            Assert.False(result.Success);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal("Shared run record was not found.", result.FailureReason);

            var queueItem = await queue.GetAsync(sharedRunId);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Equal(_controlPlaneId, queueItem.ControlPlaneId);
            Assert.Null(queueItem.ClaimToken);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("Shared run record was not found.", queueItem.Reason);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_Redis_Item_When_Dispatch_Fails()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRunId =
                RunId("shared-run-1");

            await store.CreateAsync(
                CreateSharedRun(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(sharedRunId));

            var runDispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = false,
                    SharedRunId = sharedRunId,
                    RuntimeInstanceId = "runtime-1",
                    Message = "Dispatch failed.",
                    FailureReason = "runtime queue rejected",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });

            var runtimeInstanceRegistry = await CreateReadyRuntimeRegistryAsync();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                runtimeInstanceRegistry,
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver(_controlPlaneId),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = _controlPlaneId
                }
            });

            Assert.False(result.Success);
            Assert.Equal(sharedRunId, result.SharedRunId);
            Assert.Equal("runtime queue rejected", result.FailureReason);

            var queueItem = await queue.GetAsync(sharedRunId);

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Equal(_controlPlaneId, queueItem.ControlPlaneId);
            Assert.Null(queueItem.ClaimToken);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("runtime queue rejected", queueItem.Reason);

            var sharedRun = await store.GetAsync(sharedRunId);

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, sharedRun!.Status);
            Assert.Equal(_controlPlaneId, sharedRun.ControlPlaneId);
            Assert.Null(sharedRun.LocalRunId);
            Assert.Null(sharedRun.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Allow_Only_One_Concurrent_Redis_Dispatch()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            var sharedRunId =
                RunId("shared-run-1");

            await store.CreateAsync(
                CreateSharedRun(
                    sharedRunId,
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(sharedRunId));

            var tasks = Enumerable.Range(0, 20)
                .Select(async index =>
                {
                    var runtimeInstanceId = $"runtime-{index}";
                    var runtimeInstanceRegistry = await CreateReadyRuntimeRegistryAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                    var dispatcher = new AiSharedQueueDispatcher(
                        queue,
                        store,
                        new FakeSharedRunDispatcher(
                            new AiSharedRunDispatchResult
                            {
                                Success = true,
                                SharedRunId = sharedRunId,
                                RuntimeInstanceId = runtimeInstanceId,
                                LocalRunId = $"local-run-{index}",
                                ExecutionId = $"execution-{index}",
                                Message = "Dispatched.",
                                StartedAtUtc = DateTimeOffset.UtcNow,
                                CompletedAtUtc = DateTimeOffset.UtcNow
                            }),
                        new FakeRunAdmissionController(
                            assignedRuntimeInstanceId: runtimeInstanceId),
                        new InMemoryAiRuntimeAdmissionReservationStore(),
                        runtimeInstanceRegistry,
                        new FakeRuntimeScaleOutRequestPublisher(),
                        new HardcodedAiTenantRuntimeSettingsProvider(),
                        new StaticAiControlPlaneIdResolver(_controlPlaneId),
                        new FakeExecutionContextAccessor(),
                        NullLogger<AiSharedQueueDispatcher>.Instance);

                    return await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        WorkerId = $"worker-{index}",
                        Metadata = new Dictionary<string, string>
                        {
                            ["controlPlaneId"] = _controlPlaneId
                        }
                    }).ConfigureAwait(false);
                })
                .ToArray();

            var results = await Task.WhenAll(tasks);

            var successes = results
                .Where(result => result.Success)
                .ToArray();

            var noItems = results
                .Where(result => result.NoItemAvailable)
                .ToArray();

            Assert.Single(successes);
            Assert.Equal(19, noItems.Length);

            var loadedQueueItem = await queue.GetAsync(sharedRunId);

            Assert.NotNull(loadedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loadedQueueItem!.Status);
            Assert.Equal(_controlPlaneId, loadedQueueItem.ControlPlaneId);

            var loadedRun = await store.GetAsync(sharedRunId);

            Assert.NotNull(loadedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, loadedRun!.Status);
            Assert.Equal(_controlPlaneId, loadedRun.ControlPlaneId);
            Assert.False(string.IsNullOrWhiteSpace(loadedRun.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(loadedRun.ExecutionId));
        }

        private RedisAiSharedRunStore CreateRunStore()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedRunStore(
                _connection,
                Options.Create(new RedisAiSharedRunStoreOptions
                {
                    KeyPrefix = _runKeyPrefix,
                    ListScanLimit = 100
                }),
                new StaticAiControlPlaneIdResolver(_controlPlaneId));
        }

        private RedisAiSharedQueue CreateQueue()
        {
            if (_connection is null)
            {
                throw new InvalidOperationException("Redis connection was not initialized.");
            }

            return new RedisAiSharedQueue(
                _connection,
                Options.Create(new RedisAiSharedQueueOptions
                {
                    KeyPrefix = _queueKeyPrefix,
                    ListScanLimit = 100
                }),
                new StaticAiControlPlaneIdResolver(_controlPlaneId));
        }

        private AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            AiSharedRunStatus status,
            string? tenantId = null,
            string? pipelineKey = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

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
                    PipelineName = pipelineKey ?? "pipeline-1"
                },
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = pipelineKey,
                CorrelationId = sharedRunId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = effectiveMetadata
            };
        }

        private AiSharedQueueItem CreateQueueItem(
            string sharedRunId,
            string? tenantId = null,
            string? pipelineKey = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = _controlPlaneId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = pipelineKey,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = _controlPlaneId
                }
            };
        }

        private static async Task<InMemoryAiRuntimeInstanceRegistry> CreateReadyRuntimeRegistryAsync(
            string runtimeInstanceId = "runtime-1")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        Role = AiRuntimeInstanceRole.Runtime,
                        HostName = "redis-shared-queue-dispatcher-test-host",
                        ProcessId = Environment.ProcessId,
                        WorkerCount = 1,
                        QueueCapacity = 100,
                        MaxConcurrentRuns = 1,
                        RuntimeVersion = "unit-test",
                        Metadata = new Dictionary<string, string>
                        {
                            ["test"] = "true"
                        }
                    })
                .ConfigureAwait(false);

            await registry.HeartbeatAsync(
                    runtimeInstanceId,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    availableRunSlots: 1,
                    activeWorkerCount: 0,
                    availableWorkerCount: 1,
                    maxLocalWorkersPerExecution: 1,
                    isQueuePaused: false,
                    canAcceptRun: true,
                    status: AiRuntimeInstanceStatus.Ready)
                .ConfigureAwait(false);

            return registry;
        }

        private string RunId(
            string name)
        {
            return $"{_runIdPrefix}-{name}";
        }

        private sealed class FakeSharedRunDispatcher : IAiSharedRunDispatcher
        {
            private readonly AiSharedRunDispatchResult _result;

            public FakeSharedRunDispatcher(
                AiSharedRunDispatchResult? result = null)
            {
                var now = DateTimeOffset.UtcNow;

                _result = result ?? new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    LocalRunId = "local-run-1",
                    ExecutionId = "execution-1",
                    Message = "Dispatched.",
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                };
            }

            public AiSharedRunDispatchRequest? LastRequest { get; private set; }

            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                var now = DateTimeOffset.UtcNow;

                return Task.FromResult(new AiSharedRunDispatchResult
                {
                    Success = _result.Success,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    LocalRunId = _result.LocalRunId,
                    ExecutionId = _result.ExecutionId,
                    ClaimToken = request.ClaimToken,
                    Message = _result.Message,
                    FailureReason = _result.FailureReason,
                    StartedAtUtc = _result.StartedAtUtc == default ? now : _result.StartedAtUtc,
                    CompletedAtUtc = _result.CompletedAtUtc == default ? now : _result.CompletedAtUtc,
                    DurationMs = _result.DurationMs,
                    Diagnostics = _result.Diagnostics
                });
            }
        }
    }
}