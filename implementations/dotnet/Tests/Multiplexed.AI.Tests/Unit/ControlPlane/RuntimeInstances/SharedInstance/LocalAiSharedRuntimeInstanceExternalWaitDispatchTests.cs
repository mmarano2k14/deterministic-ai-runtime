using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.SharedInstance
{
    /// <summary>
    /// Validates that normal external-wait shared dispatch is acknowledged only after the
    /// local runtime has durably bound the continuation to the expected execution.
    /// </summary>
    public sealed class LocalAiSharedRuntimeInstanceExternalWaitDispatchTests
    {
        [Fact]
        public async Task DispatchAsync_Should_Wait_For_Durable_ExternalWait_Execution_Binding()
        {
            var queue = new ControllableExternalWaitRuntimeQueueControlPlane();
            var runtime = new LocalAiSharedRuntimeInstance("runtime-1", queue);
            var sharedRun = CreateSharedRun();

            var dispatchTask = runtime.DispatchAsync(CreateDispatchRequest(sharedRun));

            await queue.EnqueueObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(dispatchTask.IsCompleted);

            queue.Accept("parent-execution-1");

            var result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(result.Success);
            Assert.Equal("local-continuation-run-1", result.LocalRunId);
            Assert.Equal("parent-execution-1", result.ExecutionId);
            Assert.True(queue.GetRunStatusCallCount > 0);
        }

        [Fact]
        public async Task DispatchAsync_Should_Reject_ExternalWait_When_Local_Run_Fails_Before_Execution_Binding()
        {
            var queue = new ControllableExternalWaitRuntimeQueueControlPlane();
            var runtime = new LocalAiSharedRuntimeInstance("runtime-1", queue);
            var sharedRun = CreateSharedRun();

            var dispatchTask = runtime.DispatchAsync(CreateDispatchRequest(sharedRun));

            await queue.EnqueueObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Fail(new InvalidOperationException("continuation-transition-failed"));

            var result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(result.Success);
            Assert.Contains("continuation-transition-failed", result.FailureReason ?? string.Empty);
            Assert.Equal("shared-continuation-1", result.SharedRunId);
        }

        private static AiSharedRuntimeInstanceDispatchRequest CreateDispatchRequest(
            AiSharedRunRecord sharedRun)
        {
            return new AiSharedRuntimeInstanceDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                SharedRun = sharedRun,
                RunRequest = sharedRun.RunRequest,
                ClaimToken = "claim-1",
                CorrelationId = "child-continuation:child-invocation-1",
                RequestedBy = "unit-test",
                Source = "child-dag-composition",
                Reason = "resume-parent-after-child-completion"
            };
        }

        private static AiSharedRunRecord CreateSharedRun()
        {
            var snapshot = new ExecutionContextSnapshot
            {
                ContextKey = "ctx-parent-1",
                Project = "tests",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = [],
                TtlSeconds = 300
            };

            var runRequest = new AiRuntimePipelineRunRequest
            {
                PipelineName = "parent-pipeline",
                ExternalWaitContinuation = new AiRuntimeExternalWaitContinuation
                {
                    ExecutionId = "parent-execution-1",
                    StepName = "research-call-site",
                    ContinuationId = "child-continuation:child-invocation-1"
                },
                ExecutionContextSnapshot = snapshot
            };

            var now = DateTimeOffset.UtcNow;
            return new AiSharedRunRecord
            {
                SharedRunId = "shared-continuation-1",
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = runRequest,
                ExecutionContextSnapshot = snapshot,
                PipelineKey = runRequest.PipelineName,
                CorrelationId = runRequest.ExternalWaitContinuation.ContinuationId,
                SubmittedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        private sealed class ControllableExternalWaitRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            private readonly TaskCompletionSource<AiExecutionRecord> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly AiRuntimeWorkerRunHandle handle;
            private volatile AiRuntimePipelineRunState state;

            public ControllableExternalWaitRuntimeQueueControlPlane()
            {
                this.handle = new AiRuntimeWorkerRunHandle(
                    "local-continuation-run-1",
                    this.completion.Task);

                this.state = CreateState(
                    status: "queued",
                    executionId: null,
                    failureReason: null);
            }

            public TaskCompletionSource<bool> EnqueueObserved { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int GetRunStatusCallCount { get; private set; }

            public void Accept(string executionId)
            {
                this.handle.MarkRunning(executionId);
                this.state = CreateState(
                    status: "running",
                    executionId: executionId,
                    failureReason: null);
            }

            public void Fail(Exception exception)
            {
                ArgumentNullException.ThrowIfNull(exception);

                this.handle.MarkFailed();
                this.state = CreateState(
                    status: "failed",
                    executionId: null,
                    failureReason: exception.Message);
                this.completion.TrySetException(exception);
            }

            public Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return request.Operation switch
                {
                    AiRuntimeQueueControlPlaneOperation.EnqueueRun => EnqueueRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.GetRunStatus => GetRunStatusAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.GetQueueStatus => GetQueueStatusAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.CancelRun => CancelRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.CancelQueuedRun => CancelQueuedRunAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.PauseQueue => PauseQueueAsync(request, cancellationToken),
                    AiRuntimeQueueControlPlaneOperation.ResumeQueue => ResumeQueueAsync(request, cancellationToken),
                    _ => throw new NotSupportedException()
                };
            }

            public Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.EnqueueObserved.TrySetResult(true);

                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.EnqueueRun,
                    Success = true,
                    Message = "queued",
                    RunId = this.handle.RunId,
                    RunHandle = this.handle,
                    RunState = this.state,
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.GetRunStatusCallCount++;
                var current = this.state;

                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.GetRunStatus,
                    Success = true,
                    RunId = current.RunId,
                    ExecutionId = current.ExecutionId,
                    RunState = current,
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = AiRuntimeQueueControlPlaneOperation.GetQueueStatus,
                    Success = true,
                    QueueState = new AiRuntimePipelineQueueState
                    {
                        RuntimeInstanceId = "runtime-1",
                        CanAcceptRun = true,
                        SnapshotAtUtc = DateTimeOffset.UtcNow
                    },
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                CreateSuccessAsync(AiRuntimeQueueControlPlaneOperation.CancelRun, cancellationToken);

            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                CreateSuccessAsync(AiRuntimeQueueControlPlaneOperation.CancelQueuedRun, cancellationToken);

            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                CreateSuccessAsync(AiRuntimeQueueControlPlaneOperation.PauseQueue, cancellationToken);

            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default) =>
                CreateSuccessAsync(AiRuntimeQueueControlPlaneOperation.ResumeQueue, cancellationToken);

            private static Task<AiRuntimeQueueControlPlaneResult> CreateSuccessAsync(
                AiRuntimeQueueControlPlaneOperation operation,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AiRuntimeQueueControlPlaneResult
                {
                    Operation = operation,
                    Success = true,
                    RuntimeInstanceId = "runtime-1",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                });
            }

            private static AiRuntimePipelineRunState CreateState(
                string status,
                string? executionId,
                string? failureReason)
            {
                return new AiRuntimePipelineRunState
                {
                    RunId = "local-continuation-run-1",
                    ExecutionId = executionId,
                    PipelineKey = "parent-pipeline",
                    PipelineName = "parent-pipeline",
                    RuntimeInstanceId = "runtime-1",
                    Status = status,
                    IsQueued = string.Equals(status, "queued", StringComparison.Ordinal),
                    IsRunning = string.Equals(status, "running", StringComparison.Ordinal),
                    FailureReason = failureReason
                };
            }
        }
    }
}
