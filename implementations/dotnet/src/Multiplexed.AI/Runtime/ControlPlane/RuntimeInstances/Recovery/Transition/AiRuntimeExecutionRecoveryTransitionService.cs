using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Recovery;

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
    /// without mutating shared queue state or runtime execution index state.
    ///
    /// When mutation is enabled, it requeues the dispatched shared queue item and
    /// then marks the local runtime execution index entry as requeued for recovery.
    /// </remarks>
    public sealed class AiRuntimeExecutionRecoveryTransitionService : IAiRuntimeExecutionRecoveryTransitionService
    {
        private const string RecoveryModeMetadataKey = "recovery.mode";
        private const string RecoveryModeResumeExistingExecution = "resume-existing-execution";
        private const string RecoveryFailedExecutionIdMetadataKey = "recovery.failedExecutionId";
        private const string RecoveryFailedRuntimeInstanceIdMetadataKey = "recovery.failedRuntimeInstanceId";
        private const string RecoveryFailedLocalRunIdMetadataKey = "recovery.failedLocalRunId";
        private const string RecoveryReasonMetadataKey = "recovery.reason";

        private readonly IAiSharedQueue sharedQueue;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;
        private readonly AiRuntimeExecutionRecoveryReconciliationOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex)
            : this(
                sharedQueue,
                runtimeRunExecutionIndex,
                Options.Create(new AiRuntimeExecutionRecoveryReconciliationOptions()))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeExecutionRecoveryTransitionService"/> class.
        /// </summary>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runtimeRunExecutionIndex">The runtime run execution index.</param>
        /// <param name="options">The runtime execution recovery reconciliation options.</param>
        public AiRuntimeExecutionRecoveryTransitionService(
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex,
            IOptions<AiRuntimeExecutionRecoveryReconciliationOptions> options)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);
            ArgumentNullException.ThrowIfNull(options);

            this.sharedQueue = sharedQueue;
            this.runtimeRunExecutionIndex = runtimeRunExecutionIndex;
            this.options = options.Value;
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

            if (string.IsNullOrWhiteSpace(ownership.LocalRunId))
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
                    Reason = "local-run-id-missing"
                };
            }

            if (string.IsNullOrWhiteSpace(ownership.ExecutionId))
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
                    Reason = "execution-id-missing"
                };
            }

            var reason =
                request.Reason ?? "runtime-execution-recovery-requeue";

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
                    Reason = reason
                };
            }

            var metadata =
                this.options.EnableDagExecutionResume
                    ? CreateDagResumeRecoveryMetadata(
                        ownership,
                        reason)
                    : null;

            var requeued = await this.sharedQueue
                .RequeueDispatchedAsync(
                    ownership.SharedRunId,
                    ownership.ClaimToken,
                    reason,
                    metadata,
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

            await this.runtimeRunExecutionIndex
                .MarkRequeuedForRecoveryAsync(
                    ownership.LocalRunId,
                    ownership.ExecutionId,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AiRuntimeExecutionRecoveryTransitionResult
            {
                Accepted = true,
                Changed = true,
                SharedRunId = ownership.SharedRunId,
                RuntimeInstanceId = ownership.RuntimeInstanceId,
                LocalRunId = ownership.LocalRunId,
                ExecutionId = ownership.ExecutionId,
                Action = "requeue-shared-run",
                Reason = reason
            };
        }

        /// <summary>
        /// Creates metadata instructing the next runtime dispatch to resume the existing durable DAG execution.
        /// </summary>
        /// <param name="ownership">The resolved shared run ownership.</param>
        /// <param name="reason">The recovery reason.</param>
        /// <returns>The recovery metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateDagResumeRecoveryMetadata(
            AiSharedRunOwnershipResolutionResult ownership,
            string reason)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RecoveryModeMetadataKey] = RecoveryModeResumeExistingExecution,
                [RecoveryFailedExecutionIdMetadataKey] = ownership.ExecutionId ?? string.Empty,
                [RecoveryFailedRuntimeInstanceIdMetadataKey] = ownership.RuntimeInstanceId ?? string.Empty,
                [RecoveryFailedLocalRunIdMetadataKey] = ownership.LocalRunId ?? string.Empty,
                [RecoveryReasonMetadataKey] = reason
            };
        }
    }
}