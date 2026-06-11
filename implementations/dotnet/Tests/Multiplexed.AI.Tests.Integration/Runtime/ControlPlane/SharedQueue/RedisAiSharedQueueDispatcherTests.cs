using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
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

            var runKeys = server.Keys(
                    database: database.Database,
                    pattern: $"{_runKeyPrefix}*")
                .ToArray();

            var queueKeys = server.Keys(
                    database: database.Database,
                    pattern: $"{_queueKeyPrefix}*")
                .ToArray();

            var keys = runKeys
                .Concat(queueKeys)
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

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher();
            var admissionController = new FakeRunAdmissionController();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                admissionController, 
                new InMemoryAiRuntimeAdmissionReservationStore(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "redis-integration-test",
                Reason = "runtime instance has capacity"
            });

            Assert.True(result.Success);
            Assert.False(result.NoItemAvailable);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);

            Assert.NotNull(result.QueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, result.QueueItem!.Status);

            Assert.NotNull(result.SharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.SharedRun!.Status);
            Assert.Equal("runtime-1", result.SharedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", result.SharedRun.LocalRunId);
            Assert.Equal("execution-1", result.SharedRun.ExecutionId);

            var loadedQueueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(loadedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loadedQueueItem!.Status);
            Assert.Equal("runtime-1", loadedQueueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("worker-1", loadedQueueItem.ClaimedByWorkerId);
            Assert.False(string.IsNullOrWhiteSpace(loadedQueueItem.ClaimToken));

            var loadedRun = await store.GetAsync("shared-run-1");

            Assert.NotNull(loadedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, loadedRun!.Status);
            Assert.Equal("runtime-1", loadedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", loadedRun.LocalRunId);
            Assert.Equal("execution-1", loadedRun.ExecutionId);

            Assert.NotNull(runDispatcher.LastRequest);
            Assert.Equal("shared-run-1", runDispatcher.LastRequest!.SharedRun.SharedRunId);
            Assert.Equal("runtime-1", runDispatcher.LastRequest.RuntimeInstanceId);
            Assert.Equal("correlation-1", runDispatcher.LastRequest.CorrelationId);
            Assert.Equal("tester", runDispatcher.LastRequest.RequestedBy);
            Assert.Equal("redis-integration-test", runDispatcher.LastRequest.Source);
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
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1"
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

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                new FakeSharedRunDispatcher(), 
                new FakeRunAdmissionController(), 
                new InMemoryAiRuntimeAdmissionReservationStore(), 
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("Shared run record was not found.", result.FailureReason);

            var queueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Null(queueItem.ClaimToken);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("Shared run record was not found.", queueItem.Reason);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_Redis_Item_When_Dispatch_Fails()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = false,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    Message = "Dispatch failed.",
                    FailureReason = "runtime queue rejected",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime queue rejected", result.FailureReason);

            var queueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem!.Status);
            Assert.Null(queueItem.ClaimToken);
            Assert.Null(queueItem.ClaimedByRuntimeInstanceId);
            Assert.Equal("runtime queue rejected", queueItem.Reason);

            var sharedRun = await store.GetAsync("shared-run-1");

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, sharedRun!.Status);
            Assert.Null(sharedRun.LocalRunId);
            Assert.Null(sharedRun.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Allow_Only_One_Concurrent_Redis_Dispatch()
        {
            var store = CreateRunStore();
            var queue = CreateQueue();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var tasks = Enumerable.Range(0, 20)
                .Select(index =>
                {
                    var dispatcher = new AiSharedQueueDispatcher(
                     queue,
                     store,
                     new FakeSharedRunDispatcher(
                         new AiSharedRunDispatchResult
                         {
                             Success = true,
                             SharedRunId = "shared-run-1",
                             RuntimeInstanceId = $"runtime-{index}",
                             LocalRunId = $"local-run-{index}",
                             ExecutionId = $"execution-{index}",
                             Message = "Dispatched.",
                             StartedAtUtc = DateTimeOffset.UtcNow,
                             CompletedAtUtc = DateTimeOffset.UtcNow
                         }),
                     new FakeRunAdmissionController(
                         assignedRuntimeInstanceId: $"runtime-{index}"), 
                     new InMemoryAiRuntimeAdmissionReservationStore(),
                     NullLogger<AiSharedQueueDispatcher>.Instance);

                    return dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = $"runtime-{index}",
                        WorkerId = $"worker-{index}"
                    });
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

            var loadedQueueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(loadedQueueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, loadedQueueItem!.Status);

            var loadedRun = await store.GetAsync("shared-run-1");

            Assert.NotNull(loadedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, loadedRun!.Status);
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
                new StaticAiControlPlaneIdResolver("test-control-plane"));
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
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        private static AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            AiSharedRunStatus status,
            string? tenantId = null,
            string? pipelineKey = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                Status = status,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = pipelineKey ?? "pipeline-1"
                },
                TenantId = tenantId,
                PipelineKey = pipelineKey,
                CorrelationId = sharedRunId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        private static AiSharedQueueItem CreateQueueItem(
            string sharedRunId,
            string? tenantId = null,
            string? pipelineKey = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                TenantId = tenantId,
                PipelineKey = pipelineKey,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now
            };
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