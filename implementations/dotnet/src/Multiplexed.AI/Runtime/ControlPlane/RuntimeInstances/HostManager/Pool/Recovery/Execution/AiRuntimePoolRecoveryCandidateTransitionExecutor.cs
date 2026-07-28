using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Centralizes candidate-level recovery validation and the existing transition boundary.
    /// </summary>
    public sealed class AiRuntimePoolRecoveryCandidateTransitionExecutor :
        IAiRuntimePoolRecoveryCandidateTransitionExecutor
    {
        private const string InFlightReason =
            "runtime-pool-claimed-in-flight-recovery";
        private const string LocalQueuedReason =
            "runtime-pool-claimed-local-queued-recovery";
        private const string UnsupportedCandidateReason =
            "unsupported-recovery-candidate-kind";

        private readonly IAiSharedRunOwnershipResolver ownershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService
            transitionService;

        public AiRuntimePoolRecoveryCandidateTransitionExecutor(
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService)
        {
            this.ownershipResolver =
                ownershipResolver
                ?? throw new ArgumentNullException(nameof(ownershipResolver));
            this.transitionService =
                transitionService
                ?? throw new ArgumentNullException(nameof(transitionService));
        }

        public async Task<IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>>
            ExecuteAsync(
                string failureId,
                IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> candidates,
                Func<AiRuntimePoolAssignedWorkCandidate, bool> isAuthorized,
                Func<CancellationToken, Task> ensureActiveLeaseAsync,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentNullException.ThrowIfNull(candidates);
            ArgumentNullException.ThrowIfNull(isAuthorized);
            ArgumentNullException.ThrowIfNull(ensureActiveLeaseAsync);

            var outcomes =
                new List<AiRuntimePoolRecoveryCandidateOutcome>(
                    candidates.Count);

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(candidate);

                if (!isAuthorized(candidate))
                {
                    throw CreateAuthorityException(
                        failureId,
                        candidate.LocalRunId,
                        AiRuntimePoolRecoveryExecutionAuthorityFailure
                            .CandidateBoundaryViolation,
                        $"Candidate '{candidate.LocalRunId}' escaped its claimed recovery inventory boundary.");
                }

                await ensureActiveLeaseAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.OtherRecoverable)
                {
                    outcomes.Add(CreateUnsupportedOutcome(candidate));
                    continue;
                }

                ValidateCandidateKind(candidate);

                var ownership =
                    await this.ownershipResolver
                        .ResolveAsync(
                            new AiSharedRunOwnershipResolutionRequest
                            {
                                RuntimeInstanceId =
                                    candidate.RuntimeInstanceId,
                                LocalRunId = candidate.LocalRunId,
                                ExecutionId = candidate.ExecutionId,
                                SharedRunId = candidate.SharedRunId,
                                TenantId = candidate.TenantId,
                                TenantGroupId = candidate.TenantGroupId
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateOwnershipBoundary(
                    failureId,
                    candidate,
                    ownership);

                var transition =
                    await this.transitionService
                        .ApplyAsync(
                            new AiRuntimeExecutionRecoveryTransitionRequest
                            {
                                Ownership = ownership,
                                DryRun = false,
                                Reason =
                                    candidate.Kind ==
                                        AiRuntimePoolAssignedWorkKind.InFlight
                                        ? InFlightReason
                                        : LocalQueuedReason
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateTransitionBoundary(
                    failureId,
                    candidate,
                    transition);

                await ensureActiveLeaseAsync(cancellationToken)
                    .ConfigureAwait(false);

                outcomes.Add(
                    new AiRuntimePoolRecoveryCandidateOutcome
                    {
                        Candidate = candidate,
                        Ownership = ownership,
                        Transition = transition,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
            }

            return outcomes;
        }

        private static void ValidateCandidateKind(
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.InFlight &&
                string.IsNullOrWhiteSpace(candidate.ExecutionId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InFlightExecutionIdMissing,
                    $"In-flight candidate '{candidate.LocalRunId}' is missing its durable ExecutionId.");
            }

            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.LocalQueued &&
                string.IsNullOrWhiteSpace(candidate.SharedRunId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LocalQueuedSharedRunIdMissing,
                    $"Local-queued candidate '{candidate.LocalRunId}' is missing its durable SharedRunId.");
            }

            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.LocalQueued &&
                !string.IsNullOrWhiteSpace(candidate.ExecutionId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .CandidateBoundaryViolation,
                    $"Local-queued candidate '{candidate.LocalRunId}' unexpectedly carries ExecutionId '{candidate.ExecutionId}'.");
            }
        }

        private static void ValidateOwnershipBoundary(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);

            var matches =
                StringComparer.Ordinal.Equals(
                    ownership.RuntimeInstanceId,
                    candidate.RuntimeInstanceId) &&
                (string.IsNullOrWhiteSpace(ownership.LocalRunId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.LocalRunId,
                     candidate.LocalRunId)) &&
                (string.IsNullOrWhiteSpace(ownership.ExecutionId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.ExecutionId,
                     candidate.ExecutionId)) &&
                (string.IsNullOrWhiteSpace(ownership.TenantId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.TenantId,
                     candidate.TenantId)) &&
                (string.IsNullOrWhiteSpace(ownership.TenantGroupId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.TenantGroupId,
                     candidate.TenantGroupId)) &&
                (string.IsNullOrWhiteSpace(ownership.SharedRunId) ||
                 string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                 StringComparer.Ordinal.Equals(
                     ownership.SharedRunId,
                     candidate.SharedRunId));

            if (!matches)
            {
                throw CreateAuthorityException(
                    failureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .OwnershipBoundaryViolation,
                    $"Ownership resolution for '{candidate.LocalRunId}' escaped the exact candidate identity boundary.");
            }

            if (ownership.Resolved)
            {
                var complete =
                    StringComparer.Ordinal.Equals(
                        ownership.LocalRunId,
                        candidate.LocalRunId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.ExecutionId,
                        candidate.ExecutionId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.TenantId,
                        candidate.TenantId) &&
                    StringComparer.Ordinal.Equals(
                        ownership.TenantGroupId,
                        candidate.TenantGroupId) &&
                    (string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                     StringComparer.Ordinal.Equals(
                         ownership.SharedRunId,
                         candidate.SharedRunId));

                if (!complete)
                {
                    throw CreateAuthorityException(
                        failureId,
                        candidate.LocalRunId,
                        AiRuntimePoolRecoveryExecutionAuthorityFailure
                            .OwnershipBoundaryViolation,
                        $"Resolved ownership for '{candidate.LocalRunId}' did not preserve its complete durable identity.");
                }
            }
        }

        private static void ValidateTransitionBoundary(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiRuntimeExecutionRecoveryTransitionResult transition)
        {
            ArgumentNullException.ThrowIfNull(transition);

            var matches =
                StringComparer.Ordinal.Equals(
                    transition.RuntimeInstanceId,
                    candidate.RuntimeInstanceId) &&
                (string.IsNullOrWhiteSpace(transition.LocalRunId) ||
                 StringComparer.Ordinal.Equals(
                     transition.LocalRunId,
                     candidate.LocalRunId)) &&
                (string.IsNullOrWhiteSpace(transition.ExecutionId) ||
                 StringComparer.Ordinal.Equals(
                     transition.ExecutionId,
                     candidate.ExecutionId)) &&
                (string.IsNullOrWhiteSpace(transition.SharedRunId) ||
                 string.IsNullOrWhiteSpace(candidate.SharedRunId) ||
                 StringComparer.Ordinal.Equals(
                     transition.SharedRunId,
                     candidate.SharedRunId));

            if (!matches)
            {
                throw CreateAuthorityException(
                    failureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .TransitionBoundaryViolation,
                    $"Recovery transition for '{candidate.LocalRunId}' returned another identity.");
            }
        }

        private static AiRuntimePoolRecoveryCandidateOutcome
            CreateUnsupportedOutcome(
                AiRuntimePoolAssignedWorkCandidate candidate)
        {
            return new AiRuntimePoolRecoveryCandidateOutcome
            {
                Candidate = candidate,
                Transition =
                    new AiRuntimeExecutionRecoveryTransitionResult
                    {
                        Accepted = false,
                        Changed = false,
                        SharedRunId = candidate.SharedRunId,
                        RuntimeInstanceId = candidate.RuntimeInstanceId,
                        LocalRunId = candidate.LocalRunId,
                        ExecutionId = candidate.ExecutionId,
                        Action = "none",
                        Reason = UnsupportedCandidateReason
                    },
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static AiRuntimePoolRecoveryExecutionAuthorityException
            CreateAuthorityException(
                string failureId,
                string? localRunId,
                AiRuntimePoolRecoveryExecutionAuthorityFailure reason,
                string message)
        {
            return new AiRuntimePoolRecoveryExecutionAuthorityException(
                failureId,
                localRunId,
                reason,
                message);
        }
    }
}
