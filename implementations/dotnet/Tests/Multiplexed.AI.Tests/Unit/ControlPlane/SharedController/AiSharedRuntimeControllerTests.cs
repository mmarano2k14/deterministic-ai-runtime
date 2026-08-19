using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Runtime.ControlPlane.ShareQueue;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    public sealed class AiSharedRuntimeControllerTests
    {
        [Fact]
        public async Task SubmitRunAsync_Should_Create_Shared_Run_Assigned_To_Instance()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1"),
                    Reason = "Runtime instance selected.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 1,
                    CurrentInstanceCount = 1
                });

            var controller = CreateController(admission);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.Run.Status);
            Assert.Equal("runtime-1", result.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.True(admission.AdmitCalled);
            Assert.Equal("shared-run-1", admission.LastRequest?.RunId);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Forward_Typed_Placement_To_Admission()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-2",
                    AssignedInstance = CreateRuntimeInstance("runtime-2"),
                    Reason = "Required runtime placement selected."
                });

            var controller = CreateController(admission);

            var placement = new AiRunPlacementDirective
            {
                Target = new AiRunPlacementTarget
                {
                    RuntimeInstanceId = "runtime-2",
                    PoolId = "pool-1",
                    NodeId = "node-1"
                },
                Requirement = AiRunPlacementRequirement.Required,
                Fallback = AiRunPlacementFallback.Reject
            };

            var result = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = "shared-run-placement",
                    RunRequest = CreateRunRequest(),
                    Placement = placement,
                    RequestedBy = "tester",
                    Source = "unit-test"
                });

            Assert.True(result.Success);
            Assert.NotNull(admission.LastRequest?.Placement);
            Assert.Equal(
                "runtime-2",
                admission.LastRequest!.Placement!.Target.RuntimeInstanceId);
            Assert.Equal(
                "pool-1",
                admission.LastRequest.Placement.Target.PoolId);
            Assert.Equal(
                "node-1",
                admission.LastRequest.Placement.Target.NodeId);
            Assert.Equal(
                AiRunPlacementRequirement.Required,
                admission.LastRequest.Placement.Requirement);
            Assert.Equal(
                AiRunPlacementFallback.Reject,
                admission.LastRequest.Placement.Fallback);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Dispatch_When_Admission_Assigns_To_Instance()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1"),
                    Reason = "Runtime instance selected."
                });

            var dispatcher = new FakeSharedRunDispatcher();

            var controller = CreateController(
                admission,
                dispatcher: dispatcher);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.Run.Status);
            Assert.Equal("runtime-1", result.Run.AssignedRuntimeInstanceId);
            Assert.Equal("local-run-1", result.Run.LocalRunId);
            Assert.Equal("execution-1", result.Run.ExecutionId);

            Assert.NotNull(dispatcher.LastRequest);
            Assert.Equal("shared-run-1", dispatcher.LastRequest!.SharedRun.SharedRunId);
            Assert.Equal("runtime-1", dispatcher.LastRequest.RuntimeInstanceId);
            Assert.Equal("correlation-1", dispatcher.LastRequest.CorrelationId);
            Assert.Equal("tester", dispatcher.LastRequest.RequestedBy);
            Assert.Equal("unit-test", dispatcher.LastRequest.Source);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Return_Assigned_Record_When_Dispatch_Fails()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1"),
                    Reason = "Runtime instance selected."
                });

            var dispatcher = new FakeSharedRunDispatcher(
                new AiSharedRunDispatchResult
                {
                    Success = false,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-1",
                    FailureReason = "dispatch failed",
                    Message = "Dispatch failed.",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });

            var controller = CreateController(
                admission,
                dispatcher: dispatcher);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.AssignedToInstance, result.Run.Status);
            Assert.Equal("runtime-1", result.Run.AssignedRuntimeInstanceId);
            Assert.Null(result.Run.LocalRunId);
            Assert.Null(result.Run.ExecutionId);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Create_QueuedGlobally_Run_When_Admission_Queues_Globally()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "No local capacity.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1
                });

            var controller = CreateController(admission);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, result.Run.Status);
            Assert.Null(result.AssignedRuntimeInstanceId);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Enqueue_SharedQueue_Item_When_Admission_Queues_Globally()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "No instance capacity."
                });

            var sharedQueue = new InMemoryAiSharedQueue();

            var controller = new AiSharedRuntimeController(
                admission,
                new InMemoryAiSharedRunStore(),
                sharedQueue,
                new FakeSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.QueuedGlobally, result.Run.Status);

            var queueItem = await sharedQueue.GetAsync("shared-run-1");

            Assert.NotNull(queueItem);
            Assert.Equal("shared-run-1", queueItem!.SharedRunId);
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItem.Status);
            Assert.Equal("tenant-1", queueItem.ExecutionContextSnapshot.TenantId);
            Assert.Equal("pipeline-1", queueItem.PipelineKey);
            Assert.Equal("No instance capacity.", queueItem.Reason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Create_ScaleOutRequested_Run_When_Admission_Requests_ScaleOut()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                    Reason = "Scale-out required.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1,
                    MaxInstanceCount = 3
                });

            var controller = CreateController(admission);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, result.Run.Status);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Publish_ScaleOut_Request_When_Admission_Requests_ScaleOut()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                    Reason = "Scale-out required.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1,
                    MaxInstanceCount = 3
                });

            var publisher = new CapturingScaleOutPublisher();

            var controller = CreateController(
                admission,
                scaleOutPublisher: publisher);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, result.Run.Status);

            Assert.NotNull(publisher.LastRequest);
            Assert.Equal("shared-run-1", publisher.LastRequest!.SharedRunId);
            Assert.Equal("tenant-1", publisher.LastRequest.TenantId);
            Assert.Equal("pipeline-1", publisher.LastRequest.PipelineKey);
            Assert.Equal(1, publisher.LastRequest.VisibleInstanceCount);
            Assert.Equal(0, publisher.LastRequest.AvailableInstanceCount);
            Assert.Equal(1, publisher.LastRequest.CurrentInstanceCount);
            Assert.Equal(3, publisher.LastRequest.MaxInstanceCount);
            Assert.Equal("correlation-1", publisher.LastRequest.CorrelationId);
            Assert.Equal("tester", publisher.LastRequest.RequestedBy);
            Assert.Equal("unit-test", publisher.LastRequest.Source);
            Assert.Equal("Scale-out required.", publisher.LastRequest.Reason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Create_Rejected_Run_When_Admission_Rejects()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.Reject,
                    Reason = "No capacity.",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 0,
                    CurrentInstanceCount = 1
                });

            var controller = CreateController(admission);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.Rejected, result.Run.Status);
            Assert.Equal("No capacity.", result.Run.FailureReason);
        }

        [Fact]
        public async Task GetRunAsync_Should_Return_Known_Run()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1")
                });

            var controller = CreateController(admission);

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            var result = await controller.GetRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun,
                SharedRunId = "shared-run-1"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal("shared-run-1", result.Run.SharedRunId);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.Run.Status);
        }

        [Fact]
        public async Task ListRunsAsync_Should_Return_Known_Runs()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1")
                });

            var controller = CreateController(admission);

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            var result = await controller.ListRunsAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.ListRuns
            });

            Assert.True(result.Success);
            Assert.Single(result.Runs);
            Assert.Equal("shared-run-1", result.Runs[0].SharedRunId);
            Assert.Equal(AiSharedRunStatus.Dispatched, result.Runs[0].Status);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Mark_Run_As_Cancelled()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queued globally."
                });

            var controller = CreateController(admission);

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            var result = await controller.CancelRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.CancelRun,
                SharedRunId = "shared-run-1",
                Reason = "operator cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.Cancelled, result.Run.Status);
            Assert.Equal("operator cancel", result.Run.FailureReason);
            Assert.Equal("tester", result.Run.RequestedBy);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Return_Same_Run_When_Already_Cancelled()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally
                });

            var controller = CreateController(admission);

            await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            await controller.CancelRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.CancelRun,
                SharedRunId = "shared-run-1",
                Reason = "first cancel"
            });

            var result = await controller.CancelRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.CancelRun,
                SharedRunId = "shared-run-1",
                Reason = "second cancel"
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRunStatus.Cancelled, result.Run.Status);
            Assert.Equal("first cancel", result.Run.FailureReason);
        }

        [Fact]
        public async Task ExecuteAsync_Should_Dispatch_By_Operation()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally
                });

            var controller = CreateController(admission);

            var result = await controller.ExecuteAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest()
            });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);
            Assert.Equal(AiSharedRuntimeControllerOperation.SubmitRun, result.Operation);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Return_Failure_When_RunRequest_Is_Missing()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally
                });

            var controller = CreateController(admission);

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun
            });

            Assert.False(result.Success);
            Assert.Contains("RunRequest is required", result.FailureReason);
        }

        [Fact]
        public async Task GetRunAsync_Should_Return_Failure_When_SharedRunId_Is_Missing()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally
                });

            var controller = CreateController(admission);

            var result = await controller.GetRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.GetRun
            });

            Assert.False(result.Success);
            Assert.Contains("SharedRunId is required", result.FailureReason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Return_Failure_When_Disabled()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1"
                });

            var controller = CreateController(
                admission,
                new AiSharedRuntimeControllerOptions
                {
                    EnableSubmitRun = false
                });

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RunRequest = CreateRunRequest()
            });

            Assert.False(result.Success);
            Assert.Contains("disabled", result.FailureReason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Record_Started_And_Completed_Events()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-1",
                    AssignedInstance = CreateRuntimeInstance("runtime-1")
                });

            var observer = new CapturingControlPlaneObserver();

            var controller = new AiSharedRuntimeController(
                admission,
                new InMemoryAiSharedRunStore(),
                new InMemoryAiSharedQueue(),
                new FakeSharedRunDispatcher(),
                new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(new AiSharedRuntimeControllerOptions()),
                observer,
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));

            var result = await controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "shared-run-1",
                RunRequest = CreateRunRequest(),
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "unit-test"
            });

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);

            Assert.All(observer.Events, controlPlaneEvent =>
            {
                Assert.Equal(AiControlPlaneArea.SharedController, controlPlaneEvent.Area);
                Assert.Equal("SubmitRun", controlPlaneEvent.Operation);
                Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
                Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
            });
        }

        /// <summary>
        /// Verifies that a circuit-open dispatch failure does not mark the shared run as dispatched.
        /// </summary>
        [Fact]
        public async Task SubmitRunAsync_Should_Not_Mark_Run_Dispatched_When_Dispatch_Fails_With_CircuitOpen()
        {
            var admission =
                new FakeRunAdmissionController(
                    new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                        AssignedRuntimeInstanceId = "runtime-1",
                        AssignedInstance = CreateRuntimeInstance("runtime-1"),
                        Reason = "Runtime instance selected."
                    });

            var dispatcher =
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

            var controller =
                CreateController(
                    admission,
                    dispatcher: dispatcher);

            var result =
                await controller.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = "shared-run-1",
                        RunRequest = CreateRunRequest(),
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test"
                    });

            Assert.True(result.Success);
            Assert.NotNull(result.Run);

            Assert.Equal(
                AiSharedRunStatus.AssignedToInstance,
                result.Run.Status);

            Assert.Equal(
                "runtime-1",
                result.Run.AssignedRuntimeInstanceId);

            Assert.Null(result.Run.LocalRunId);
            Assert.Null(result.Run.ExecutionId);

            Assert.Equal(
                "http-circuit-open",
                result.Run.FailureReason);

            Assert.Equal(
                "http-circuit-open",
                result.FailureReason);
        }

        /// <summary>
        /// Verifies that a circuit-open dispatch failure reason is preserved when the shared run is read back from the store.
        /// </summary>
        [Fact]
        public async Task SubmitRunAsync_Should_Persist_Dispatch_FailureReason_When_Dispatch_Fails_With_CircuitOpen()
        {
            var admission =
                new FakeRunAdmissionController(
                    new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                        AssignedRuntimeInstanceId = "runtime-1",
                        AssignedInstance = CreateRuntimeInstance("runtime-1"),
                        Reason = "Runtime instance selected."
                    });

            var dispatcher =
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

            var controller =
                CreateController(
                    admission,
                    dispatcher: dispatcher);

            var submitResult =
                await controller.SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = "shared-run-1",
                        RunRequest = CreateRunRequest(),
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test"
                    });

            var getResult =
                await controller.GetRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.GetRun,
                        SharedRunId = "shared-run-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "tester",
                        Source = "unit-test"
                    });

            Assert.True(submitResult.Success);
            Assert.NotNull(submitResult.Run);

            Assert.Equal(
                "http-circuit-open",
                submitResult.Run.FailureReason);

            Assert.True(getResult.Success);
            Assert.NotNull(getResult.Run);

            Assert.Equal(
                AiSharedRunStatus.AssignedToInstance,
                getResult.Run.Status);

            Assert.Null(getResult.Run.LocalRunId);
            Assert.Null(getResult.Run.ExecutionId);

            Assert.Equal(
                "http-circuit-open",
                getResult.Run.FailureReason);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Converge_Duplicate_ExternalWait_QueueFirst_Physical_Submission()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue parent continuation."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            var request = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "child-continuation-physical-1",
                SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                PipelineKey = "parent-pipeline",
                RunRequest = CreateExternalWaitContinuationRunRequest("continuation-1"),
                Source = "unit-test"
            };

            var first = await controller.SubmitRunAsync(request);
            var second = await controller.SubmitRunAsync(request);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(first.SharedRunId, second.SharedRunId);
            Assert.Equal(
                "continuation-1",
                second.Run?.RunRequest.ExternalWaitContinuation?.ContinuationId);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
            Assert.Equal(1, admission.AdmitCallCount);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Requeue_Same_Deterministic_ExternalWait_Run_When_Bound_Local_Attempt_Failed()
        {
            const string sharedRunId = "child-continuation-physical-redrive-1";
            const string failedRuntimeInstanceId = "runtime-failed-1";
            const string failedLocalRunId = "local-continuation-failed-1";
            const string executionId = "parent-execution-1";

            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue parent continuation."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var runExecutionIndex = new FakeRuntimeRunExecutionIndex();
            var controller = CreateController(
                admission,
                store: store,
                sharedQueue: queue,
                runtimeRunExecutionIndex: runExecutionIndex);
            var request = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = sharedRunId,
                SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                PipelineKey = "parent-pipeline",
                RunRequest = CreateExternalWaitContinuationRunRequest("continuation-redrive-1"),
                Source = "unit-test"
            };

            var first = await controller.SubmitRunAsync(request);
            Assert.True(first.Success);

            var claimed = await queue.ClaimAsync(
                sharedRunId,
                new AiSharedQueueClaimRequest
                {
                    RuntimeInstanceId = "mcp-control-plane",
                    ControlPlaneId = "test-control-plane",
                    TenantId = "tenant-1",
                    PipelineKey = "parent-pipeline",
                    WorkerId = "mcp-background-pump"
                });

            Assert.NotNull(claimed);
            Assert.NotNull(
                await queue.MarkDispatchedAsync(
                    sharedRunId,
                    claimed!.ClaimToken!));
            Assert.NotNull(
                await store.MarkDispatchedAsync(
                    sharedRunId,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    executionId));

            runExecutionIndex.RegisteredEntries.Add(
                new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = failedLocalRunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = failedRuntimeInstanceId,
                    Status = "failed",
                    FailureReason = "continuation-failed-after-binding",
                    ExecutionContextSnapshot = first.Run!.ExecutionContextSnapshot,
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
                    StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-20),
                    CompletedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10)
                });

            var second = await controller.SubmitRunAsync(request);

            Assert.True(second.Success);
            Assert.Equal(sharedRunId, second.SharedRunId);

            var requeued = await queue.GetAsync(sharedRunId);
            Assert.NotNull(requeued);
            Assert.Equal(AiSharedQueueItemStatus.Pending, requeued!.Status);
            Assert.Null(requeued.ClaimToken);
            Assert.Equal(
                failedRuntimeInstanceId,
                requeued.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal(
                failedLocalRunId,
                requeued.Metadata["recovery.failedLocalRunId"]);
            Assert.False(requeued.Metadata.ContainsKey("recovery.mode"));
            Assert.Equal(1, admission.AdmitCallCount);

            var durableRun = await store.GetAsync(sharedRunId);
            Assert.NotNull(durableRun);
            Assert.Equal(AiSharedRunStatus.Dispatched, durableRun!.Status);
            Assert.Equal(failedRuntimeInstanceId, durableRun.AssignedRuntimeInstanceId);
            Assert.Equal(failedLocalRunId, durableRun.LocalRunId);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Reject_Conflicting_ExternalWait_Physical_Duplicate()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue parent continuation."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            const string sharedRunId = "child-continuation-physical-2";

            var first = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "parent-pipeline",
                    RunRequest = CreateExternalWaitContinuationRunRequest("continuation-1")
                });

            var conflict = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "parent-pipeline",
                    RunRequest = CreateExternalWaitContinuationRunRequest("different-continuation")
                });

            Assert.True(first.Success);
            Assert.False(conflict.Success);
            Assert.Contains("incompatible", conflict.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Converge_Duplicate_Preallocated_QueueFirst_Submissions()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue deterministic child run."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            var request = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "child-execution-execution-42",
                SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                PipelineKey = "child-analysis",
                RunRequest = CreatePreallocatedRunRequest("execution-42"),
                Source = "unit-test"
            };

            var first = await controller.SubmitRunAsync(request);
            var second = await controller.SubmitRunAsync(request);

            Assert.True(first.Success);
            Assert.True(second.Success);
            Assert.Equal(first.SharedRunId, second.SharedRunId);
            Assert.Equal("execution-42", second.Run?.RunRequest.RequestedExecutionId);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
            Assert.Equal(1, admission.AdmitCallCount);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Converge_Concurrent_Preallocated_QueueFirst_Submissions()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue deterministic child run."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            var request = new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "child-execution-execution-47",
                SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                PipelineKey = "child-analysis",
                RunRequest = CreatePreallocatedRunRequest("execution-47"),
                Source = "unit-test"
            };

            var results = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(_ => controller.SubmitRunAsync(request)));

            Assert.All(results, result => Assert.True(result.Success));
            Assert.All(results, result => Assert.Equal("child-execution-execution-47", result.SharedRunId));
            Assert.All(results, result => Assert.Equal("execution-47", result.Run?.RunRequest.RequestedExecutionId));
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Repair_Missing_Queue_Item_For_Preallocated_Shared_Run()
        {
            var admissionDecision = new AiRunAdmissionDecision
            {
                DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                Reason = "Queue deterministic child run."
            };
            var admission = new FakeRunAdmissionController(admissionDecision);
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var snapshot = AiExecutionContextSnapshotTestFactory.Create(tenantId: "tenant-1");
            var sharedRunId = "child-execution-execution-43";
            var runRequest = CreatePreallocatedRunRequest("execution-43");

            await store.CreateAsync(
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = runRequest.PipelineName,
                        RequestedExecutionId = runRequest.RequestedExecutionId,
                        PipelineDefinitionSnapshot = runRequest.PipelineDefinitionSnapshot,
                        PipelineJson = runRequest.PipelineJson,
                        ExecutionContextSnapshot = snapshot
                    },
                    ExecutionContextSnapshot = snapshot,
                    AdmissionDecision = admissionDecision,
                    PipelineKey = "child-analysis",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });

            var controller = CreateController(admission, store: store, sharedQueue: queue);
            var result = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    RunRequest = runRequest,
                    Source = "unit-test"
                });

            Assert.True(result.Success);
            var queued = Assert.Single(await queue.ListAsync(includeTerminal: true));
            Assert.Equal(sharedRunId, queued.SharedRunId);
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Reject_Conflicting_Preallocated_Duplicate()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue deterministic child run."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            const string sharedRunId = "child-execution-execution-44";

            var first = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    RunRequest = CreatePreallocatedRunRequest("execution-44")
                });

            var conflict = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    RunRequest = CreatePreallocatedRunRequest("different-execution")
                });

            Assert.True(first.Success);
            Assert.False(conflict.Success);
            Assert.Contains("incompatible", conflict.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Reject_Conflicting_Frozen_Definition_Snapshot_For_Preallocated_Duplicate()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue deterministic child run."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            const string sharedRunId = "child-execution-execution-46";

            var first = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    RunRequest = CreatePreallocatedRunRequest("execution-46")
                });

            var conflict = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "child-analysis",
                        RequestedExecutionId = "execution-46",
                        PipelineDefinitionSnapshot = AiStoredPayload.Inline(
                            "{}",
                            contentType: "application/json",
                            contentHash: "definition-hash-v2"),
                        PipelineJson = "{\"Name\":\"child-analysis\",\"Version\":\"v1\",\"ExecutionMode\":1,\"Steps\":[]}"
                    }
                });

            Assert.True(first.Success);
            Assert.False(conflict.Success);
            Assert.Contains("incompatible", conflict.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
        }

        [Fact]
        public async Task SubmitRunAsync_Should_Reject_Conflicting_Frozen_Input_Digest_For_Preallocated_Duplicate()
        {
            var admission = new FakeRunAdmissionController(
                new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.QueueGlobally,
                    Reason = "Queue deterministic child run."
                });
            var store = new InMemoryAiSharedRunStore();
            var queue = new InMemoryAiSharedQueue();
            var controller = CreateController(admission, store: store, sharedQueue: queue);
            const string sharedRunId = "child-execution-execution-45";

            var first = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    Metadata = new Dictionary<string, string>
                    {
                        ["child.input.digest"] = "digest-a"
                    },
                    RunRequest = CreatePreallocatedRunRequest("execution-45")
                });

            var conflict = await controller.SubmitRunAsync(
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    RequestedSharedRunId = sharedRunId,
                    SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                    PipelineKey = "child-analysis",
                    Metadata = new Dictionary<string, string>
                    {
                        ["child.input.digest"] = "digest-b"
                    },
                    RunRequest = CreatePreallocatedRunRequest("execution-45")
                });

            Assert.True(first.Success);
            Assert.False(conflict.Success);
            Assert.Contains("incompatible", conflict.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await store.ListAsync(includeCancelled: true, includeCompleted: true, includeFailed: true));
            Assert.Single(await queue.ListAsync(includeTerminal: true));
        }

        private static AiSharedRuntimeController CreateController(
            IAiRunAdmissionController admissionController,
            AiSharedRuntimeControllerOptions? options = null,
            IAiSharedRunDispatcher? dispatcher = null,
            IAiRuntimeScaleOutRequestPublisher? scaleOutPublisher = null,
            IAiSharedRunStore? store = null,
            IAiSharedQueue? sharedQueue = null,
            IAiRuntimeRunExecutionIndex? runtimeRunExecutionIndex = null)
        {
            return new AiSharedRuntimeController(
                admissionController,
                store ?? new InMemoryAiSharedRunStore(),
                sharedQueue ?? new InMemoryAiSharedQueue(),
                dispatcher ?? new FakeSharedRunDispatcher(),
                scaleOutPublisher ?? new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                new HardcodedAiTenantRuntimeSettingsProvider(),
                Options.Create(options ?? new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")),
                runtimeRunExecutionIndex: runtimeRunExecutionIndex);
        }

        private static AiRuntimePipelineRunRequest CreateRunRequest()
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1"
            };
        }

        private static AiRuntimePipelineRunRequest CreateExternalWaitContinuationRunRequest(string continuationId)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "parent-pipeline",
                ExternalWaitContinuation = new AiRuntimeExternalWaitContinuation
                {
                    ExecutionId = "parent-execution-1",
                    StepName = "research-call-site",
                    ContinuationId = continuationId
                }
            };
        }

        private static AiRuntimePipelineRunRequest CreatePreallocatedRunRequest(string executionId)
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "child-analysis",
                RequestedExecutionId = executionId,
                PipelineDefinitionSnapshot = AiStoredPayload.Inline(
                    "{}",
                    contentType: "application/json",
                    contentHash: "definition-hash-v1"),
                PipelineJson = "{\"Name\":\"child-analysis\",\"Version\":\"v1\",\"ExecutionMode\":1,\"Steps\":[]}"
            };
        }

        private static AiRuntimeInstanceSnapshot CreateRuntimeInstance(
            string runtimeInstanceId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 4,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                QueueCapacity = 8,
                MaxConcurrentRuns = 2,
                AvailableRunSlots = 2,
                IsQueuePaused = false,
                CanAcceptRun = true,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now
            };
        }

        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            public List<AiControlPlaneEvent> Events { get; } = new();

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(controlPlaneEvent);

                return Task.CompletedTask;
            }
        }

        private sealed class FakeRunAdmissionController : IAiRunAdmissionController
        {
            private readonly AiRunAdmissionDecision _decision;

            public FakeRunAdmissionController(
                AiRunAdmissionDecision decision)
            {
                _decision = decision;
            }

            public bool AdmitCalled { get; private set; }

            public int AdmitCallCount { get; private set; }

            public AiRunAdmissionRequest? LastRequest { get; private set; }

            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                AdmitCalled = true;
                AdmitCallCount++;
                LastRequest = request;

                return Task.FromResult(_decision);
            }
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

        private sealed class CapturingScaleOutPublisher : IAiRuntimeScaleOutRequestPublisher
        {
            public AiRuntimeScaleOutRequest? LastRequest { get; private set; }

            public Task<AiRuntimeScaleOutRequestResult> PublishAsync(
                AiRuntimeScaleOutRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                return Task.FromResult(new AiRuntimeScaleOutRequestResult
                {
                    Success = true,
                    SharedRunId = request.SharedRunId,
                    ScaleOutRequestId = $"test-scale-out-{request.SharedRunId}",
                    RequestedTargetInstanceCount = request.CurrentInstanceCount + 1,
                    Message = "Scale-out request captured.",
                    PublishedAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
    }
}