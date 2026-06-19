using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeQueue
{
    public sealed class AiRuntimeQueueControlPlaneTests
    {
        [Fact]
        public async Task EnqueueRunAsync_Should_Call_Controller_And_Return_Handle_States()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.EnqueueRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1"
                }
            });

            Assert.True(result.Success);
            Assert.NotNull(result.RunHandle);
            Assert.NotNull(result.RunState);
            Assert.NotNull(result.QueueState);
            Assert.True(controller.EnqueueCalled);
            Assert.Equal("pipeline-1", controller.LastRunRequest?.PipelineName);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Call_Controller_With_RunId_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.CancelRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                RunId = "run-1",
                Reason = "operator cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelRunCalled);
            Assert.Equal("run-1", controller.LastRunId);
            Assert.Equal("operator cancel", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
        }

        [Fact]
        public async Task CancelQueuedRunAsync_Should_Call_Controller_With_RunId_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.CancelQueuedRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelQueuedRun,
                RunId = "run-1",
                Reason = "queued cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelQueuedRunCalled);
            Assert.Equal("run-1", controller.LastRunId);
            Assert.Equal("queued cancel", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Call_Controller_With_Reason_And_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                Reason = "maintenance",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.PauseQueueCalled);
            Assert.Equal("maintenance", controller.LastReason);
            Assert.Equal("tester", controller.LastRequestedBy);
            Assert.NotNull(result.QueueState);
            Assert.True(result.QueueState.IsPaused);
        }

        [Fact]
        public async Task ResumeQueueAsync_Should_Call_Controller_With_RequestedBy()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.ResumeQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.ResumeQueue,
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.ResumeQueueCalled);
            Assert.Equal("tester", controller.LastRequestedBy);
            Assert.NotNull(result.QueueState);
            Assert.False(result.QueueState.IsPaused);
        }

        [Fact]
        public async Task GetRunStatusAsync_Should_Call_GetRunStateAsync()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(
                runExecutionIndex,
                status: "running");

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.GetRunStatusAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                RunId = "run-1",
                IncludeRunState = true
            });

            Assert.True(result.Success);
            Assert.True(controller.GetRunStateCalled);
            Assert.NotNull(result.RunState);
            Assert.Equal("run-1", result.RunState.RunId);
        }

        [Fact]
        public async Task GetQueueStatusAsync_Should_Call_GetQueueStateAsync()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.GetQueueStatusAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.GetQueueStatus
            });

            Assert.True(result.Success);
            Assert.True(controller.GetQueueStateCalled);
            Assert.NotNull(result.QueueState);
        }

        [Fact]
        public async Task ExecuteAsync_Should_Dispatch_By_Operation()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            await RegisterIndexedRunAsync(runExecutionIndex);

            var controlPlane = CreateControlPlane(
                controller,
                runExecutionIndex: runExecutionIndex);

            var result = await controlPlane.ExecuteAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun,
                RunId = "run-1",
                Reason = "dispatch cancel",
                RequestedBy = "tester"
            });

            Assert.True(result.Success);
            Assert.True(controller.CancelRunCalled);
            Assert.Equal(AiRuntimeQueueControlPlaneOperation.CancelRun, result.Operation);
        }

        [Fact]
        public async Task CancelRunAsync_Should_Return_Failure_When_RunId_Is_Missing()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.CancelRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.CancelRun
            });

            Assert.False(result.Success);
            Assert.Contains("RunId is required", result.FailureReason);
        }

        [Fact]
        public async Task EnqueueRunAsync_Should_Return_Failure_When_RunRequest_Is_Missing()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var controlPlane = CreateControlPlane(controller);

            var result = await controlPlane.EnqueueRunAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun
            });

            Assert.False(result.Success);
            Assert.Contains("RunRequest is required", result.FailureReason);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Return_Failure_When_Disabled()
        {
            var controller = new FakeRuntimePipelineBackgroundController();

            var controlPlane = CreateControlPlane(
                controller,
                new AiRuntimeQueueControlPlaneOptions
                {
                    EnablePauseQueue = false
                });

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue
            });

            Assert.False(result.Success);
            Assert.Contains("disabled", result.FailureReason);
        }

        [Fact]
        public async Task PauseQueueAsync_Should_Record_Started_And_Completed_Events()
        {
            var controller = new FakeRuntimePipelineBackgroundController();
            var observer = new CapturingControlPlaneObserver();

            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();

            var controlPlane = new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex,
                Options.Create(new AiRuntimeQueueControlPlaneOptions()),
                observer);

            var result = await controlPlane.PauseQueueAsync(new AiRuntimeQueueControlPlaneRequest
            {
                Operation = AiRuntimeQueueControlPlaneOperation.PauseQueue,
                CorrelationId = "correlation-1",
                RuntimeInstanceId = "runtime-instance-1",
                Source = "unit-test",
                RequestedBy = "tester",
                Reason = "maintenance"
            });

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);

            Assert.All(observer.Events, controlPlaneEvent =>
            {
                Assert.Equal(AiControlPlaneArea.RunControl, controlPlaneEvent.Area);
                Assert.Equal("PauseQueue", controlPlaneEvent.Operation);
                Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
                Assert.Equal("runtime-instance-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            });
        }

        private static AiRuntimeQueueControlPlane CreateControlPlane(
            IAiRuntimePipelineBackgroundController controller,
            AiRuntimeQueueControlPlaneOptions? options = null,
            IAiRuntimeRunExecutionIndex? runExecutionIndex = null)
        {
            return new AiRuntimeQueueControlPlane(
                controller,
                runExecutionIndex ?? new InMemoryAiRuntimeRunExecutionIndex(),
                Options.Create(options ?? new AiRuntimeQueueControlPlaneOptions()),
                new NoopAiControlPlaneObserver());
        }

        private static async Task RegisterIndexedRunAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string runId = "run-1",
            string executionId = "execution-1",
            string runtimeInstanceId = "runtime-instance-1",
            string status = "running")
        {
            await runExecutionIndex
                .RegisterQueuedAsync(
                    new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = runId,
                        ExecutionId = executionId,
                        RuntimeInstanceId = runtimeInstanceId,
                        Status = status,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["source"] = "unit-test",
                            ["requestedBy"] = "tester",
                            ["tenantId"] = "unit-test-tenant"
                        }
                    })
                .ConfigureAwait(false);
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"unit-test-context-{Guid.NewGuid():N}",
                TenantId = "unit-test-tenant",
                TenantGroupId = "unit-test-tenant-group",
                Project = "deterministic-ai-runtime-tests",
                UserId = "unit-test-user",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>
                    {
                        new()
                        {
                            Name = "default",
                            Trns = new HashSet<string>
                            {
                                "trn:deterministic-ai-runtime-tests:runtime:run:read",
                                "trn:deterministic-ai-runtime-tests:runtime:run:write",
                                "trn:deterministic-ai-runtime-tests:runtime:execution:read"
                            }
                        }
                    },
                InFlightCount = 0,
                TtlSeconds = 300
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

        private sealed class FakeRuntimePipelineBackgroundController : IAiRuntimePipelineBackgroundController
        {
            private readonly AiRuntimeWorkerRunHandle _handle;

            public FakeRuntimePipelineBackgroundController()
            {
                var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                completionSource.SetResult(new AiExecutionRecord
                {
                    ExecutionId = "execution-1",
                    Status = AiExecutionStatus.Completed,
                    CompletedAtUtc = DateTime.UtcNow
                });

                _handle = new AiRuntimeWorkerRunHandle(
                    "run-1",
                    completionSource.Task);

                _handle.MarkRunning("execution-1");
            }

            public bool EnqueueCalled { get; private set; }

            public bool CancelRunCalled { get; private set; }

            public bool CancelQueuedRunCalled { get; private set; }

            public bool PauseQueueCalled { get; private set; }

            public bool ResumeQueueCalled { get; private set; }

            public bool GetRunStateCalled { get; private set; }

            public bool GetQueueStateCalled { get; private set; }

            public string? LastRunId { get; private set; }

            public string? LastReason { get; private set; }

            public string? LastRequestedBy { get; private set; }

            public AiRuntimePipelineRunRequest? LastRunRequest { get; private set; }

            public Task StartAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                EnqueueCalled = true;
                LastRunRequest = request;

                return ValueTask.FromResult(_handle);
            }

            public Task PauseQueueAsync(
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                PauseQueueCalled = true;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                return Task.CompletedTask;
            }

            public Task ResumeQueueAsync(
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                ResumeQueueCalled = true;
                LastRequestedBy = requestedBy;

                return Task.CompletedTask;
            }

            public Task<bool> CancelQueuedRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                CancelQueuedRunCalled = true;
                LastRunId = runId;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                return Task.FromResult(true);
            }

            public Task<bool> CancelRunAsync(
                string runId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                CancelRunCalled = true;
                LastRunId = runId;
                LastReason = reason;
                LastRequestedBy = requestedBy;

                return Task.FromResult(true);
            }

            public Task<AiRuntimePipelineRunState?> GetRunStateAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                GetRunStateCalled = true;
                LastRunId = runId;

                return Task.FromResult<AiRuntimePipelineRunState?>(
                    new AiRuntimePipelineRunState
                    {
                        RunId = runId,
                        ExecutionId = "execution-1",
                        PipelineKey = "pipeline-1",
                        PipelineName = "pipeline-1",
                        RuntimeInstanceId = "runtime-instance-1",
                        Status = "running",
                        IsQueued = false,
                        IsRunning = true,
                        CancellationRequested = false
                    });
            }

            public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(
                CancellationToken cancellationToken = default)
            {
                GetQueueStateCalled = true;

                return Task.FromResult(new AiRuntimePipelineQueueState
                {
                    RuntimeInstanceId = "runtime-instance-1",
                    IsPaused = PauseQueueCalled && !ResumeQueueCalled,
                    QueuedRunCount = 1,
                    RunningRunCount = 1,
                    ActiveRunCount = 2,
                    QueueCapacity = 8,
                    MaxConcurrentRuns = 1,
                    AvailableRunSlots = 0,
                    CanAcceptRun = false,
                    SnapshotAtUtc = DateTimeOffset.UtcNow
                });
            }
        }
    }
}