using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    public sealed class AiSharedQueueDispatcherTests
    {
        [Fact]
        public async Task DispatchNextAsync_Should_Return_NoItemAvailable_When_Queue_Is_Empty()
        {
            var dispatcher = new AiSharedQueueDispatcher(
                new InMemoryAiSharedQueue(),
                new InMemoryAiSharedRunStore(),
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController());

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.True(result.NoItemAvailable);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Dispatch_Claimed_Item_And_Update_State()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher();
            var fakeAdmission = new FakeRunAdmissionController();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                fakeAdmission);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "instance available"
            });

            Assert.True(result.Success);
            Assert.False(result.NoItemAvailable);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.NotNull(result.QueueItem);
            Assert.NotNull(result.SharedRun);
            Assert.NotNull(result.DispatchResult);

            Assert.Equal(AiSharedQueueItemStatus.Dispatched, result.QueueItem!.Status);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.SharedRun!.Status);
            Assert.Equal("local-run-1", result.SharedRun.LocalRunId);
            Assert.Equal("execution-1", result.SharedRun.ExecutionId);
            Assert.Equal("runtime-1", result.SharedRun.AssignedRuntimeInstanceId);

            var queueItem = await queue.GetAsync("shared-run-1");
            Assert.NotNull(queueItem);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, queueItem!.Status);

            var sharedRun = await store.GetAsync("shared-run-1");
            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal("local-run-1", sharedRun.LocalRunId);
            Assert.Equal("execution-1", sharedRun.ExecutionId);

            Assert.NotNull(runDispatcher.LastRequest);
            Assert.Equal("shared-run-1", runDispatcher.LastRequest!.SharedRun.SharedRunId);
            Assert.Equal("runtime-1", runDispatcher.LastRequest.RuntimeInstanceId);
            Assert.Equal("correlation-1", runDispatcher.LastRequest.CorrelationId);
            Assert.Equal("tester", runDispatcher.LastRequest.RequestedBy);
            Assert.Equal("unit-test", runDispatcher.LastRequest.Source);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_When_SharedRun_Is_Missing()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController());

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
            Assert.Equal("Shared run record was not found.", queueItem.Reason);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_When_Dispatch_Fails()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = false,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    FailureReason = "runtime queue rejected",
                    Message = "Dispatch failed.",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController());

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
            Assert.Equal("runtime queue rejected", queueItem.Reason);

            var sharedRun = await store.GetAsync("shared-run-1");

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, sharedRun!.Status);
            Assert.Null(sharedRun.LocalRunId);
            Assert.Null(sharedRun.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Respect_Tenant_And_Pipeline_Filters()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally,
                    tenantId: "tenant-a",
                    pipelineKey: "pipeline-a"));

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-2",
                    AiSharedRunStatus.QueuedGlobally,
                    tenantId: "tenant-b",
                    pipelineKey: "pipeline-b"));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1", tenantId: "tenant-a", pipelineKey: "pipeline-a"));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-2", tenantId: "tenant-b", pipelineKey: "pipeline-b"));

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController());

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                TenantId = "tenant-b",
                PipelineKey = "pipeline-b"
            });

            Assert.True(result.Success);
            Assert.Equal("shared-run-2", result.SharedRunId);

            var firstItem = await queue.GetAsync("shared-run-1");
            var secondItem = await queue.GetAsync("shared-run-2");

            Assert.NotNull(firstItem);
            Assert.NotNull(secondItem);
            Assert.Equal(AiSharedQueueItemStatus.Pending, firstItem!.Status);
            Assert.Equal(AiSharedQueueItemStatus.Dispatched, secondItem!.Status);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Merge_Metadata()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally,
                    metadata: new Dictionary<string, string>
                    {
                        ["tenant"] = "tenant-1",
                        ["priority"] = "normal"
                    }));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher();
            var admissionController = new FakeRunAdmissionController();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                admissionController);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                Metadata = new Dictionary<string, string>
                {
                    ["priority"] = "high",
                    ["source"] = "queue-dispatcher-test"
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(runDispatcher.LastRequest);
            Assert.Equal("tenant-1", runDispatcher.LastRequest!.Metadata["tenant"]);
            Assert.Equal("high", runDispatcher.LastRequest.Metadata["priority"]);
            Assert.Equal("queue-dispatcher-test", runDispatcher.LastRequest.Metadata["source"]);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Throw_When_Request_Is_Null()
        {
            var dispatcher = new AiSharedQueueDispatcher(
                new InMemoryAiSharedQueue(),
                new InMemoryAiSharedRunStore(),
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController());

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                dispatcher.DispatchNextAsync(null!));
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Throw_When_RuntimeInstanceId_Is_Missing()
        {
            var dispatcher = new AiSharedQueueDispatcher(
                new InMemoryAiSharedQueue(),
                new InMemoryAiSharedRunStore(),
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController());

            await Assert.ThrowsAsync<ArgumentException>(() =>
                dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = " "
                }));
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