using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Runtime pipeline background controller fake.
    /// </summary>
    public sealed class FakeRuntimePipelineBackgroundController : IAiRuntimePipelineBackgroundController
    {
        private readonly AiRuntimeWorkerRunHandle _handle;

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeRuntimePipelineBackgroundController"/> class.
        /// </summary>
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

        public bool EnqueueResumeCalled { get; private set; }

        public bool CancelRunCalled { get; private set; }

        public bool CancelQueuedRunCalled { get; private set; }

        public bool PauseQueueCalled { get; private set; }

        public bool ResumeQueueCalled { get; private set; }

        public bool GetRunStateCalled { get; private set; }

        public bool GetQueueStateCalled { get; private set; }

        public string? LastRunId { get; private set; }

        public string? LastExecutionId { get; private set; }

        public string? LastReason { get; private set; }

        public string? LastRequestedBy { get; private set; }

        public AiRuntimePipelineRunRequest? LastRunRequest { get; private set; }

        /// <inheritdoc />
        public Task StartAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<AiRuntimeWorkerRunHandle> EnqueueAsync(
            AiRuntimePipelineRunRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            EnqueueCalled = true;
            LastRunRequest = request;

            return ValueTask.FromResult(_handle);
        }

        /// <inheritdoc />
        public ValueTask<AiRuntimeWorkerRunHandle> EnqueueResumeAsync(
            AiRuntimePipelineRunRequest request,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            cancellationToken.ThrowIfCancellationRequested();

            EnqueueResumeCalled = true;
            LastRunRequest = request;
            LastExecutionId = executionId;

            var completionSource = new TaskCompletionSource<AiExecutionRecord>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            completionSource.SetResult(new AiExecutionRecord
            {
                ExecutionId = executionId,
                Status = AiExecutionStatus.Completed,
                CompletedAtUtc = DateTime.UtcNow
            });

            var handle = new AiRuntimeWorkerRunHandle(
                "run-1",
                completionSource.Task,
                executionId);

            handle.MarkRunning(executionId);

            return ValueTask.FromResult(handle);
        }

        /// <inheritdoc />
        public Task PauseQueueAsync(
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PauseQueueCalled = true;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ResumeQueueAsync(
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ResumeQueueCalled = true;
            LastRequestedBy = requestedBy;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<bool> CancelQueuedRunAsync(
            string runId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            CancelQueuedRunCalled = true;
            LastRunId = runId;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<bool> CancelRunAsync(
            string runId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            CancelRunCalled = true;
            LastRunId = runId;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<AiRuntimePipelineRunState?> GetRunStateAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            cancellationToken.ThrowIfCancellationRequested();

            GetRunStateCalled = true;
            LastRunId = runId;

            return Task.FromResult<AiRuntimePipelineRunState?>(
                new AiRuntimePipelineRunState
                {
                    RunId = runId,
                    ExecutionId = LastExecutionId ?? "execution-1",
                    PipelineKey = "pipeline-1",
                    PipelineName = "pipeline-1",
                    RuntimeInstanceId = "runtime-instance-1",
                    Status = "running",
                    IsQueued = false,
                    IsRunning = true,
                    CancellationRequested = false
                });
        }

        /// <inheritdoc />
        public Task<AiRuntimePipelineQueueState> GetQueueStateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
