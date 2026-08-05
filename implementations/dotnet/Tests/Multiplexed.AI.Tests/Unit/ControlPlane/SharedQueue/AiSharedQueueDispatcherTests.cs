using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
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
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
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
            var reservationStore = new InMemoryAiRuntimeAdmissionReservationStore();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                fakeAdmission,
                reservationStore,
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

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
        public async Task DispatchNextAsync_Should_Persist_Canonical_Runtime_When_A_Retry_Target_Reports_Existing_Acceptance()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun("shared-run-1", AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var now = DateTimeOffset.UtcNow;
            var runDispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-canonical-winner",
                    LocalRunId = "local-run-canonical",
                    ExecutionId = "execution-canonical",
                    Message = "Existing recovery acceptance resolved.",
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                });

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "ambiguous recovery dispatch retry"
            });

            Assert.True(result.Success);
            Assert.Equal("runtime-canonical-winner", result.RuntimeInstanceId);
            Assert.Equal("runtime-1", runDispatcher.LastRequest!.RuntimeInstanceId);

            var sharedRun = await store.GetAsync("shared-run-1");

            Assert.NotNull(sharedRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, sharedRun!.Status);
            Assert.Equal("runtime-canonical-winner", sharedRun.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-canonical", sharedRun.LocalRunId);
            Assert.Equal("execution-canonical", sharedRun.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Heal_Stale_Queue_Item_From_Durable_Ownership_Without_Runtime_Call()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.Dispatched,
                    assignedRuntimeInstanceId: "runtime-existing",
                    localRunId: "local-existing",
                    executionId: "execution-existing"));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(
                new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });

            Assert.True(result.Success);
            Assert.Equal("runtime-existing", result.RuntimeInstanceId);
            Assert.Equal(0, runDispatcher.CallCount);

            var queueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                queueItem!.Status);

            var stored = await store.GetAsync("shared-run-1");

            Assert.NotNull(stored);
            Assert.Equal(
                "runtime-existing",
                stored!.AssignedRuntimeInstanceId);
            Assert.Equal("local-existing", stored.LocalRunId);
            Assert.Equal("execution-existing", stored.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Release_Failed_Durable_Ownership_And_Redispatch_Recovery_Item()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.Dispatched,
                    assignedRuntimeInstanceId: "runtime-failed-1",
                    localRunId: "run-failed-1",
                    executionId: "execution-existing-1"));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1",
                    metadata: new Dictionary<string, string>
                    {
                        ["recovery.mode"] = "resume-existing-execution",
                        ["recovery.failedExecutionId"] = "execution-existing-1",
                        ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                        ["recovery.failedLocalRunId"] = "run-failed-1",
                        ["recovery.reason"] = "forced-pod-deletion"
                    }));

            var now = DateTimeOffset.UtcNow;
            var runDispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    LocalRunId = "run-replacement-1",
                    ExecutionId = "execution-existing-1",
                    Message = "Recovery redispatched.",
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                });

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(
                new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });

            Assert.True(result.Success);
            Assert.Equal(1, runDispatcher.CallCount);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.NotNull(runDispatcher.LastRequest);
            Assert.Equal(
                "resume-existing-execution",
                runDispatcher.LastRequest!.Metadata["recovery.mode"]);
            Assert.Equal(
                "runtime-failed-1",
                runDispatcher.LastRequest.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal(
                "run-failed-1",
                runDispatcher.LastRequest.Metadata["recovery.failedLocalRunId"]);

            var queueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                queueItem!.Status);

            var stored = await store.GetAsync("shared-run-1");

            Assert.NotNull(stored);
            Assert.Equal(AiSharedRunStatus.Dispatched, stored!.Status);
            Assert.Equal("runtime-1", stored.AssignedRuntimeInstanceId);
            Assert.Equal("run-replacement-1", stored.LocalRunId);
            Assert.Equal("execution-existing-1", stored.ExecutionId);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Not_Requeue_Committed_Dispatch_When_Lifecycle_Journal_Fails()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem("shared-run-1"));

            var runDispatcher = new FakeSharedRunDispatcher();

            var dispatcher = new AiSharedQueueDispatcher(
                queue,
                store,
                runDispatcher,
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                runtimeSignalPublisher: null,
                lifecycleJournal:
                    new ThrowingRuntimeLifecycleJournal());

            var result = await dispatcher.DispatchNextAsync(
                new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });

            Assert.True(result.Success);
            Assert.Equal(1, runDispatcher.CallCount);

            var queueItem = await queue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal(
                AiSharedQueueItemStatus.Dispatched,
                queueItem!.Status);

            var stored = await store.GetAsync("shared-run-1");

            Assert.NotNull(stored);
            Assert.Equal(AiSharedRunStatus.Dispatched, stored!.Status);
            Assert.Equal("runtime-1", stored.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", stored.LocalRunId);
            Assert.Equal("execution-1", stored.ExecutionId);

            var secondResult = await dispatcher.DispatchNextAsync(
                new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                });

            Assert.False(secondResult.Success);
            Assert.True(secondResult.NoItemAvailable);
            Assert.Equal(1, runDispatcher.CallCount);
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
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
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
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
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
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

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
                admissionController,
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

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
        public async Task DispatchNextAsync_Should_Preserve_Recovery_Resume_Metadata_When_Dispatching()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally,
                    metadata: new Dictionary<string, string>
                    {
                        ["recovery.mode"] = "resume-existing-execution",
                        ["recovery.failedExecutionId"] = "execution-existing-1",
                        ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                        ["recovery.failedLocalRunId"] = "run-failed-1",
                        ["recovery.reason"] = "unit-test-recovery"
                    }));

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
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            var result = await dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                WorkerId = "worker-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test",
                Reason = "dispatch recovery resume",
                Metadata = new Dictionary<string, string>
                {
                    ["request.marker"] = "dispatch-request"
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(runDispatcher.LastRequest);

            Assert.Equal(
                "resume-existing-execution",
                runDispatcher.LastRequest!.Metadata["recovery.mode"]);

            Assert.Equal(
                "execution-existing-1",
                runDispatcher.LastRequest.Metadata["recovery.failedExecutionId"]);

            Assert.Equal(
                "runtime-failed-1",
                runDispatcher.LastRequest.Metadata["recovery.failedRuntimeInstanceId"]);

            Assert.Equal(
                "run-failed-1",
                runDispatcher.LastRequest.Metadata["recovery.failedLocalRunId"]);

            Assert.Equal(
                "unit-test-recovery",
                runDispatcher.LastRequest.Metadata["recovery.reason"]);

            Assert.Equal(
                "dispatch-request",
                runDispatcher.LastRequest.Metadata["request.marker"]);
        }

        [Fact]
        public async Task DispatchNextAsync_Should_Throw_When_Request_Is_Null()
        {
            var dispatcher = new AiSharedQueueDispatcher(
                new InMemoryAiSharedQueue(),
                new InMemoryAiSharedRunStore(),
                new FakeSharedRunDispatcher(),
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

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
                new FakeRunAdmissionController(),
                new InMemoryAiRuntimeAdmissionReservationStore(),
                await CreateReadyRuntimeRegistryAsync(),
                new FakeRuntimeScaleOutRequestPublisher(),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                new FakeExecutionContextAccessor(),
                NullLogger<AiSharedQueueDispatcher>.Instance);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                dispatcher.DispatchNextAsync(new AiSharedQueueDispatchRequest
                {
                    RuntimeInstanceId = " "
                }));
        }

        /// <summary>
        /// Verifies that a circuit-open dispatch failure from the shared queue is not marked as dispatched
        /// and that the dispatch failure reason is persisted on the shared run record.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_And_Persist_FailureReason_When_Dispatch_Fails_With_CircuitOpen()
        {
            var queue =
                new InMemoryAiSharedQueue();

            var store =
                new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1"));

            var runDispatcher =
                new FakeSharedRunDispatcher(
                    new AiSharedRunDispatchResult
                    {
                        Success = false,
                        SharedRunId = "shared-run-1",
                        RuntimeInstanceId = "runtime-1",
                        FailureReason = "http-circuit-open",
                        Message = "HTTP runtime circuit breaker is open.",
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });

            var dispatcher =
                new AiSharedQueueDispatcher(
                    queue,
                    store,
                    runDispatcher,
                    new FakeRunAdmissionController(),
                    new InMemoryAiRuntimeAdmissionReservationStore(),
                    await CreateReadyRuntimeRegistryAsync(),
                    new FakeRuntimeScaleOutRequestPublisher(),
                    new HardcodedAiTenantRuntimeSettingsProvider(),
                    new StaticAiControlPlaneIdResolver("control-plane-1"),
                    new FakeExecutionContextAccessor(),
                    NullLogger<AiSharedQueueDispatcher>.Instance);

            var result =
                await dispatcher.DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "runtime-1",
                        WorkerId = "worker-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test",
                        Reason = "dispatch from shared queue"
                    });

            Assert.False(result.Success);
            Assert.False(result.NoItemAvailable);

            Assert.Equal(
                "shared-run-1",
                result.SharedRunId);

            Assert.Equal(
                "runtime-1",
                result.RuntimeInstanceId);

            Assert.Equal(
                "http-circuit-open",
                result.FailureReason);

            var queueItem =
                await queue.GetAsync(
                    "shared-run-1");

            Assert.NotNull(queueItem);

            Assert.Equal(
                AiSharedQueueItemStatus.Pending,
                queueItem!.Status);

            Assert.Equal(
                "http-circuit-open",
                queueItem.Reason);

            var sharedRun =
                await store.GetAsync(
                    "shared-run-1");

            Assert.NotNull(sharedRun);

            Assert.Equal(
                AiSharedRunStatus.QueuedGlobally,
                sharedRun!.Status);

            Assert.Null(sharedRun.LocalRunId);
            Assert.Null(sharedRun.ExecutionId);

            Assert.Equal(
                "runtime-1",
                sharedRun.AssignedRuntimeInstanceId);

            Assert.Equal(
                "http-circuit-open",
                sharedRun.FailureReason);
        }

        /// <summary>
        /// Verifies that successful queue-less dispatch keeps its reservation
        /// until a post-acceptance heartbeat refreshes capacity.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Hold_QueueLess_Reservation_Until_PostAcceptance_Heartbeat()
        {
            var queue = new InMemoryAiSharedQueue();
            var store = new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally,
                    tenantId: "tenant-queue-less"));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1",
                    tenantId: "tenant-queue-less"));

            var reservationStore =
                new TrackingRuntimeAdmissionReservationStore();

            var registry =
                await CreateReadyRuntimeRegistryAsync(
                    tenantId: "tenant-queue-less");

            var dispatcher =
                new AiSharedQueueDispatcher(
                    queue,
                    store,
                    new FakeSharedRunDispatcher(),
                    new FakeRunAdmissionController(),
                    reservationStore,
                    registry,
                    new FakeRuntimeScaleOutRequestPublisher(),
                    new StaticLocalQueueCapacitySettingsProvider(0),
                    new StaticAiControlPlaneIdResolver("control-plane-1"),
                    new FakeExecutionContextAccessor(),
                    NullLogger<AiSharedQueueDispatcher>.Instance,
                    new NoopAiRuntimeRecoveryForensicsRecorder(),
                    queuePumpOptions:
                        Options.Create(
                            new AiSharedQueuePumpOptions
                            {
                                QueueLessDispatchReservationHandoffTimeout =
                                    TimeSpan.FromSeconds(2)
                            }));

            var result =
                await dispatcher.DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "runtime-1",
                        WorkerId = "worker-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test",
                        Reason = "queue-less reservation handoff"
                    });

            Assert.True(result.Success);
            Assert.Equal(1, reservationStore.ReserveCallCount);
            Assert.Equal(0, reservationStore.ReleaseCallCount);

            await registry.HeartbeatAsync(
                    "runtime-1",
                    queuedRunCount: 0,
                    runningRunCount: 1,
                    activeRunCount: 1,
                    availableRunSlots: 0,
                    activeWorkerCount: 1,
                    availableWorkerCount: 0,
                    maxLocalWorkersPerExecution: 1,
                    isQueuePaused: false,
                    canAcceptRun: false,
                    status: AiRuntimeInstanceStatus.Ready)
                .ConfigureAwait(false);

            await WaitUntilAsync(
                    () => reservationStore.ReleaseCallCount == 1,
                    TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            Assert.Equal(
                "runtime-1",
                reservationStore.LastReleasedRuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that temporary admission capacity is released when dispatch fails with circuit-open.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Release_Reservation_When_Dispatch_Fails_With_CircuitOpen()
        {
            var queue =
                new InMemoryAiSharedQueue();

            var store =
                new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1"));

            var reservationStore =
                new TrackingRuntimeAdmissionReservationStore();

            var runDispatcher =
                new FakeSharedRunDispatcher(
                    new AiSharedRunDispatchResult
                    {
                        Success = false,
                        SharedRunId = "shared-run-1",
                        RuntimeInstanceId = "runtime-1",
                        FailureReason = "http-circuit-open",
                        Message = "HTTP runtime circuit breaker is open.",
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });

            var dispatcher =
                new AiSharedQueueDispatcher(
                    queue,
                    store,
                    runDispatcher,
                    new FakeRunAdmissionController(),
                    reservationStore,
                    await CreateReadyRuntimeRegistryAsync(),
                    new FakeRuntimeScaleOutRequestPublisher(),
                    new HardcodedAiTenantRuntimeSettingsProvider(),
                    new StaticAiControlPlaneIdResolver("control-plane-1"),
                    new FakeExecutionContextAccessor(),
                    NullLogger<AiSharedQueueDispatcher>.Instance);

            var result =
                await dispatcher.DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "runtime-1",
                        WorkerId = "worker-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test",
                        Reason = "dispatch from shared queue"
                    });

            Assert.False(result.Success);

            Assert.Equal(
                "http-circuit-open",
                result.FailureReason);

            Assert.Equal(
                1,
                reservationStore.ReserveCallCount);

            Assert.Equal(
                1,
                reservationStore.ReleaseCallCount);

            Assert.Equal(
                "runtime-1",
                reservationStore.LastReservedRuntimeInstanceId);

            Assert.Equal(
                "runtime-1",
                reservationStore.LastReleasedRuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that temporary admission capacity is released when dispatch throws an exception.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Release_Reservation_When_Dispatch_Throws()
        {
            var queue =
                new InMemoryAiSharedQueue();

            var store =
                new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1"));

            var reservationStore =
                new TrackingRuntimeAdmissionReservationStore();

            var runDispatcher =
                new ThrowingSharedRunDispatcher(
                    new InvalidOperationException(
                        "HTTP runtime dispatch exploded."));

            var dispatcher =
                new AiSharedQueueDispatcher(
                    queue,
                    store,
                    runDispatcher,
                    new FakeRunAdmissionController(),
                    reservationStore,
                    await CreateReadyRuntimeRegistryAsync(),
                    new FakeRuntimeScaleOutRequestPublisher(),
                    new HardcodedAiTenantRuntimeSettingsProvider(),
                    new StaticAiControlPlaneIdResolver("control-plane-1"),
                    new FakeExecutionContextAccessor(),
                    NullLogger<AiSharedQueueDispatcher>.Instance);

            var result =
                await dispatcher.DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "runtime-1",
                        WorkerId = "worker-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test",
                        Reason = "dispatch from shared queue"
                    });

            Assert.False(result.Success);

            Assert.Equal(
                "runtime-1",
                result.RuntimeInstanceId);

            Assert.Equal(
                "HTTP runtime dispatch exploded.",
                result.FailureReason);

            Assert.Equal(
                1,
                reservationStore.ReserveCallCount);

            Assert.Equal(
                1,
                reservationStore.ReleaseCallCount);

            Assert.Equal(
                "runtime-1",
                reservationStore.LastReservedRuntimeInstanceId);

            Assert.Equal(
                "runtime-1",
                reservationStore.LastReleasedRuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that a claimed queue item is requeued when dispatch throws an exception.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Requeue_When_Dispatch_Throws()
        {
            var queue =
                new InMemoryAiSharedQueue();

            var store =
                new InMemoryAiSharedRunStore();

            await store.CreateAsync(
                CreateSharedRun(
                    "shared-run-1",
                    AiSharedRunStatus.QueuedGlobally));

            await queue.EnqueueAsync(
                CreateQueueItem(
                    "shared-run-1"));

            var runDispatcher =
                new ThrowingSharedRunDispatcher(
                    new InvalidOperationException(
                        "HTTP runtime dispatch exploded."));

            var dispatcher =
                new AiSharedQueueDispatcher(
                    queue,
                    store,
                    runDispatcher,
                    new FakeRunAdmissionController(),
                    new InMemoryAiRuntimeAdmissionReservationStore(),
                    await CreateReadyRuntimeRegistryAsync(),
                    new FakeRuntimeScaleOutRequestPublisher(),
                    new HardcodedAiTenantRuntimeSettingsProvider(),
                    new StaticAiControlPlaneIdResolver("control-plane-1"),
                    new FakeExecutionContextAccessor(),
                    NullLogger<AiSharedQueueDispatcher>.Instance);

            var result =
                await dispatcher.DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "runtime-1",
                        WorkerId = "worker-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test",
                        Reason = "dispatch from shared queue"
                    });

            Assert.False(result.Success);

            Assert.Equal(
                "HTTP runtime dispatch exploded.",
                result.FailureReason);

            var queueItem =
                await queue.GetAsync(
                    "shared-run-1");

            Assert.NotNull(queueItem);

            Assert.Equal(
                AiSharedQueueItemStatus.Pending,
                queueItem!.Status);

            Assert.Equal(
                "HTTP runtime dispatch exploded.",
                queueItem.Reason);
        }

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(condition);

            var deadlineUtc =
                DateTimeOffset.UtcNow + timeout;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadlineUtc)
                {
                    throw new TimeoutException(
                        "Timed out while waiting for the expected test condition.");
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(20))
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates a ready runtime instance registry entry used by tests that must reach
        /// the shared run dispatcher after the runtime routability guard.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <param name="tenantGroupId">The optional tenant group identifier.</param>
        /// <returns>The populated in-memory runtime instance registry.</returns>
        private static async Task<InMemoryAiRuntimeInstanceRegistry> CreateReadyRuntimeRegistryAsync(
            string runtimeInstanceId = "runtime-1",
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        TenantId = tenantId,
                        TenantGroupId = tenantGroupId,
                        Role = AiRuntimeInstanceRole.Runtime,
                        HostName = "unit-test-host",
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

        private static AiSharedRunRecord CreateSharedRun(
            string sharedRunId,
            AiSharedRunStatus status,
            string? tenantId = null,
            string? pipelineKey = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            string? assignedRuntimeInstanceId = null,
            string? localRunId = null,
            string? executionId = null)
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
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = pipelineKey,
                CorrelationId = sharedRunId,
                AssignedRuntimeInstanceId =
                    assignedRuntimeInstanceId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        private static AiSharedQueueItem CreateQueueItem(
            string sharedRunId,
            string? tenantId = null,
            string? pipelineKey = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueItem
            {
                SharedRunId = sharedRunId,
                Status = AiSharedQueueItemStatus.Pending,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: tenantId),
                PipelineKey = pipelineKey,
                EnqueuedAtUtc = now,
                UpdatedAtUtc = now,
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Shared run dispatcher that throws during dispatch.
        /// </summary>
        private sealed class ThrowingSharedRunDispatcher : IAiSharedRunDispatcher
        {
            private readonly Exception _exception;

            /// <summary>
            /// Initializes a new instance of the <see cref="ThrowingSharedRunDispatcher"/> class.
            /// </summary>
            /// <param name="exception">The exception to throw.</param>
            public ThrowingSharedRunDispatcher(
                Exception exception)
            {
                _exception =
                    exception
                    ?? throw new ArgumentNullException(nameof(exception));
            }

            /// <inheritdoc />
            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                cancellationToken.ThrowIfCancellationRequested();

                throw _exception;
            }
        }

        /// <summary>
        /// Tracks reservation reserve/release calls for shared queue dispatcher tests.
        /// </summary>
        private sealed class TrackingRuntimeAdmissionReservationStore : IAiRuntimeAdmissionReservationStore
        {
            private int _reserveCallCount;
            private int _releaseCallCount;
            private string? _lastReservedRuntimeInstanceId;
            private string? _lastReleasedRuntimeInstanceId;

            public int ReserveCallCount =>
                Volatile.Read(ref _reserveCallCount);

            public int ReleaseCallCount =>
                Volatile.Read(ref _releaseCallCount);

            public string? LastReservedRuntimeInstanceId =>
                Volatile.Read(ref _lastReservedRuntimeInstanceId);

            public string? LastReleasedRuntimeInstanceId =>
                Volatile.Read(ref _lastReleasedRuntimeInstanceId);

            /// <inheritdoc />
            public Task ReserveAsync(
                string runtimeInstanceId,
                int runCount,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                Interlocked.Increment(ref _reserveCallCount);
                Volatile.Write(
                    ref _lastReservedRuntimeInstanceId,
                    runtimeInstanceId);

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task ReleaseAsync(
                string runtimeInstanceId,
                int runCount,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                Interlocked.Increment(ref _releaseCallCount);
                Volatile.Write(
                    ref _lastReleasedRuntimeInstanceId,
                    runtimeInstanceId);

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> GetReservedRunCountAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                cancellationToken.ThrowIfCancellationRequested();

                if (string.Equals(
                        runtimeInstanceId,
                        this.LastReservedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        this.ReserveCallCount - this.ReleaseCallCount);
                }

                return Task.FromResult(0);
            }
        }

        private sealed class StaticLocalQueueCapacitySettingsProvider :
            IAiTenantRuntimeSettingsProvider
        {
            private readonly int? _localQueueCapacity;

            public StaticLocalQueueCapacitySettingsProvider(
                int? localQueueCapacity)
            {
                _localQueueCapacity = localQueueCapacity;
            }

            public AiTenantRuntimeSettings GetSettings(
                string? tenantId,
                string? tenantGroupId)
            {
                return new AiTenantRuntimeSettings
                {
                    TenantId =
                        string.IsNullOrWhiteSpace(tenantId)
                            ? "tenant-test"
                            : tenantId,
                    TenantGroupId = tenantGroupId,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                    AllowSharedFallback = true,
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = _localQueueCapacity,
                    RuntimeInstanceIdPrefix = "runtime"
                };
            }
        }

        private sealed class ThrowingRuntimeLifecycleJournal :
            IAiRuntimeLifecycleJournal
        {
            public Task AppendAsync(
                AiRuntimeLifecycleEvent lifecycleEvent,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Lifecycle journal unavailable.");
            }

            public Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(
                string eventId,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Lifecycle journal unavailable.");
            }

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByControlPlaneIdAsync(
                    string controlPlaneId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByPoolIdAsync(
                    string poolId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByKubernetesPodUidAsync(
                    string kubernetesPodUid,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByRuntimeInstanceIdAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByRuntimeFailureIncidentIdAsync(
                    string runtimeFailureIncidentId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListBySharedRunIdAsync(
                    string tenantId,
                    string sharedRunId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByExecutionIdAsync(
                    string executionId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ListByCorrelationIdAsync(
                    string correlationId,
                    CancellationToken cancellationToken = default) =>
                ThrowList();

            private static Task<IReadOnlyList<AiRuntimeLifecycleEvent>>
                ThrowList()
            {
                throw new InvalidOperationException(
                    "Lifecycle journal unavailable.");
            }
        }

        private sealed class FakeSharedRunDispatcher : IAiSharedRunDispatcher
        {
            private readonly AiSharedRunDispatchResult _result;
            private int _callCount;

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

            public int CallCount =>
                Volatile.Read(ref _callCount);

            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _callCount);
                LastRequest = request;

                var now = DateTimeOffset.UtcNow;

                return Task.FromResult(new AiSharedRunDispatchResult
                {
                    Success = _result.Success,
                    SharedRunId = request.SharedRun.SharedRunId,
                    RuntimeInstanceId =
                        string.IsNullOrWhiteSpace(_result.RuntimeInstanceId)
                            ? request.RuntimeInstanceId
                            : _result.RuntimeInstanceId,
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