using Multiplexed.Abstractions.AI.Execution.Control;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class FakeExecutionControlService : IAiExecutionControlService
    {
        public string? LastExecutionId { get; private set; }

        public string? LastReason { get; private set; }

        public string? LastRequestedBy { get; private set; }

        public string? LastRecoveryOwnerId { get; private set; }

        public string? LastWaitingKey { get; private set; }

        public string? LastWaitingStepName { get; private set; }

        public IReadOnlyDictionary<string, object?>? LastInput { get; private set; }

        public bool GetStateCalled { get; private set; }

        public bool PauseForRecoveryCalled { get; private set; }

        public bool ResumeFromRecoveryCalled { get; private set; }

        public Task<AiExecutionControlState> PauseExecutionAsync(
            string executionId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Pausing,
                    AiExecutionControlAction.Pause,
                    requestedBy,
                    reason));
        }

        public Task<AiExecutionControlState> ResumeExecutionAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.Resume,
                    requestedBy,
                    reason: null));
        }

        public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
            string executionId,
            string recoveryOwnerId,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastReason = reason;
            LastRequestedBy = recoveryOwnerId;
            LastRecoveryOwnerId = recoveryOwnerId;
            PauseForRecoveryCalled = true;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Pausing,
                    AiExecutionControlAction.Pause,
                    recoveryOwnerId,
                    reason));
        }

        public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
            string executionId,
            string recoveryOwnerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastRequestedBy = recoveryOwnerId;
            LastRecoveryOwnerId = recoveryOwnerId;
            ResumeFromRecoveryCalled = true;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.Resume,
                    recoveryOwnerId,
                    reason: null));
        }

        public Task<AiExecutionControlState> CancelExecutionAsync(
            string executionId,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Cancelling,
                    AiExecutionControlAction.Cancel,
                    requestedBy,
                    reason));
        }

        public Task<AiExecutionControlState> MarkWaitingForInputAsync(
            string executionId,
            string waitingKey,
            string? waitingStepName = null,
            string? reason = null,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastWaitingKey = waitingKey;
            LastWaitingStepName = waitingStepName;
            LastReason = reason;
            LastRequestedBy = requestedBy;

            var state =
                CreateState(
                    executionId,
                    AiExecutionControlStatus.WaitingForInput,
                    AiExecutionControlAction.WaitForInput,
                    requestedBy,
                    reason);

            state.WaitingKey = waitingKey;
            state.WaitingStepName = waitingStepName;

            return Task.FromResult(state);
        }

        public Task<AiExecutionControlState> SubmitHumanInputAsync(
            string executionId,
            string waitingKey,
            IReadOnlyDictionary<string, object?> input,
            string? submittedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastWaitingKey = waitingKey;
            LastInput = input;
            LastRequestedBy = submittedBy;

            var state =
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Resuming,
                    AiExecutionControlAction.SubmitInput,
                    submittedBy,
                    reason: null);

            state.WaitingKey = waitingKey;
            state.Input =
                new Dictionary<string, object?>(
                    input,
                    StringComparer.Ordinal);

            return Task.FromResult(state);
        }

        public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;

            return Task.FromResult(
                new AiExecutionControlDecision
                {
                    CanContinue = true,
                    Status = AiExecutionControlStatus.Running
                });
        }

        public Task<AiExecutionControlState> MarkPausedAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Paused,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
        }

        public Task<AiExecutionControlState> MarkRunningAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Running,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
        }

        public Task<AiExecutionControlState> MarkCancelledAsync(
            string executionId,
            string? requestedBy = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            LastRequestedBy = requestedBy;

            return Task.FromResult(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Cancelled,
                    AiExecutionControlAction.None,
                    requestedBy,
                    reason: null));
        }

        public Task<AiExecutionControlState?> GetStateAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastExecutionId = executionId;
            GetStateCalled = true;

            return Task.FromResult<AiExecutionControlState?>(
                CreateState(
                    executionId,
                    AiExecutionControlStatus.Running,
                    AiExecutionControlAction.None,
                    requestedBy: null,
                    reason: null));
        }

        private static AiExecutionControlState CreateState(
            string executionId,
            AiExecutionControlStatus status,
            AiExecutionControlAction pendingAction,
            string? requestedBy,
            string? reason)
        {
            return new AiExecutionControlState
            {
                ExecutionId = executionId,
                Status = status,
                PendingAction = pendingAction,
                RequestedBy = requestedBy,
                Reason = reason,
                Version = 1,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
    }
}
