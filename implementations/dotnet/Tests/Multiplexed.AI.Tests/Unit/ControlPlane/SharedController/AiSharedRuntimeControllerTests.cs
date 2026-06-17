using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Store;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
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

        private static AiSharedRuntimeController CreateController(
            IAiRunAdmissionController admissionController,
            AiSharedRuntimeControllerOptions? options = null,
            IAiSharedRunDispatcher? dispatcher = null,
            IAiRuntimeScaleOutRequestPublisher? scaleOutPublisher = null)
        {
            return new AiSharedRuntimeController(
                admissionController,
                new InMemoryAiSharedRunStore(),
                new InMemoryAiSharedQueue(),
                dispatcher ?? new FakeSharedRunDispatcher(),
                scaleOutPublisher ?? new NoopAiRuntimeScaleOutRequestPublisher(),
                new StaticAiControlPlaneIdResolver("test-control-plane"),
                Options.Create(options ?? new AiSharedRuntimeControllerOptions()),
                new NoopAiControlPlaneObserver(),
                new FakeExecutionContextSnapshotProvider(
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-1")));
        }

        private static AiRuntimePipelineRunRequest CreateRunRequest()
        {
            return new AiRuntimePipelineRunRequest
            {
                PipelineName = "pipeline-1"
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

            public AiRunAdmissionRequest? LastRequest { get; private set; }

            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                AdmitCalled = true;
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