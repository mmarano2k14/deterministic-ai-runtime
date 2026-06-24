using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Default runtime execution recovery transition service.
    /// </summary>
    /// <remarks>
    /// This service owns mutation boundaries for runtime execution recovery.
    ///
    /// It does not detect runtime health, scan runtime instances, restart hosts,
    /// kill processes, or decide which runtime instance should be recovered.
    ///
    /// When dry-run is enabled, it validates the transition and reports the action
    /// without mutating shared queue state.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryTransitionService : IAiRuntimeExecutionRecoveryTransitionService
    {
        private readonly IAiSharedQueue sharedQueue;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);

            this.sharedQueue = sharedQueue;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeExecutionRecoveryTransitionResult> ApplyAsync(
            AiRuntimeExecutionRecoveryTransitionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var ownership = request.Ownership;

            if (!ownership.Resolved)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "ownership-not-resolved"
                };
            }

            if (!ownership.CanRecover)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "ownership-not-recoverable"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.SharedRunId))
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "shared-run-id-missing"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.ClaimToken))
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "claim-token-missing"
                };
            }

            if (request.DryRun)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = true,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "dry-run-requeue-shared-run",
                    Reason = request.Reason ?? "dry-run-recovery-transition"
                };
            }

            var requeued = await sharedQueue
                .RequeueDispatchedAsync(
                    ownership.SharedRunId,
                    ownership.ClaimToken,
                    request.Reason ?? "runtime-execution-recovery-requeue",
                    cancellationToken)
                .ConfigureAwait(false);

            if (requeued is null)
            {
                return new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = false,
                    Changed = false,
                    SharedRunId = ownership.SharedRunId,
                    RuntimeInstanceId = ownership.RuntimeInstanceId,
                    LocalRunId = ownership.LocalRunId,
                    ExecutionId = ownership.ExecutionId,
                    Action = "none",
                    Reason = "shared-queue-requeue-dispatched-rejected"
                };
            }

            return new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = true,
                Changed = true,
                SharedRunId = ownership.SharedRunId,
                RuntimeInstanceId = ownership.RuntimeInstanceId,
                LocalRunId = ownership.LocalRunId,
                ExecutionId = ownership.ExecutionId,
                Action = "requeue-shared-run",
                Reason = request.Reason ?? "runtime-execution-recovery-requeue"
            };
        }
    }
}