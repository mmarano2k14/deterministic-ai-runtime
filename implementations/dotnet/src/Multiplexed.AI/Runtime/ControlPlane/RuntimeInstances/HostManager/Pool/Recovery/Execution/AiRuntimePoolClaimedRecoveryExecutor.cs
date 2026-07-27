using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Executes existing runtime recovery transitions under one exact active claim lease.
    /// </summary>
    public sealed class AiRuntimePoolClaimedRecoveryExecutor :
        IAiRuntimePoolClaimedRecoveryExecutor
    {
        private const string InFlightReason =
            "runtime-pool-claimed-in-flight-recovery";

        private const string LocalQueuedReason =
            "runtime-pool-claimed-local-queued-recovery";

        private const string UnsupportedCandidateReason =
            "unsupported-recovery-candidate-kind";

        private readonly IAiRuntimePoolRecoveryClaimStore claimStore;
        private readonly IAiSharedRunOwnershipResolver ownershipResolver;
        private readonly IAiRuntimeExecutionRecoveryTransitionService
            transitionService;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolClaimedRecoveryExecutor"/> class.
        /// </summary>
        public AiRuntimePoolClaimedRecoveryExecutor(
            IAiRuntimePoolRecoveryClaimStore claimStore,
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService)
        {
            this.claimStore =
                claimStore
                ?? throw new ArgumentNullException(nameof(claimStore));

            this.ownershipResolver =
                ownershipResolver
                ?? throw new ArgumentNullException(nameof(ownershipResolver));

            this.transitionService =
                transitionService
                ?? throw new ArgumentNullException(
                    nameof(transitionService));
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolClaimedRecoveryExecutionResult>
            ExecuteAsync(
                AiRuntimePoolClaimedAssignedWork claimedWork,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(claimedWork);

            ValidateClaimedAuthority(claimedWork);

            await this.EnsureActiveLeaseAsync(
                    claimedWork,
                    cancellationToken)
                .ConfigureAwait(false);

            var inventory =
                claimedWork.Inventory;

            var outcomes =
                new List<AiRuntimePoolRecoveryCandidateOutcome>(
                    inventory.Candidates.Count);

            foreach (var candidate in inventory.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ValidateCandidateAuthority(
                    inventory,
                    candidate);

                await this.EnsureActiveLeaseAsync(
                        claimedWork,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.OtherRecoverable)
                {
                    outcomes.Add(
                        CreateUnsupportedOutcome(candidate));

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
                                LocalRunId =
                                    candidate.LocalRunId,
                                ExecutionId =
                                    candidate.ExecutionId,
                                SharedRunId =
                                    candidate.SharedRunId,
                                TenantId =
                                    candidate.TenantId,
                                TenantGroupId =
                                    candidate.TenantGroupId
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateOwnershipBoundary(
                    claimedWork.Claim.FailureId,
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
                                        AiRuntimePoolAssignedWorkKind
                                            .InFlight
                                        ? InFlightReason
                                        : LocalQueuedReason
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                ValidateTransitionBoundary(
                    claimedWork.Claim.FailureId,
                    candidate,
                    transition);

                await this.EnsureActiveLeaseAsync(
                        claimedWork,
                        cancellationToken)
                    .ConfigureAwait(false);

                outcomes.Add(
                    new AiRuntimePoolRecoveryCandidateOutcome
                    {
                        Candidate = candidate,
                        Ownership = ownership,
                        Transition = transition,
                        CompletedAtUtc =
                            DateTimeOffset.UtcNow
                    });
            }

            return new AiRuntimePoolClaimedRecoveryExecutionResult
            {
                ClaimId = claimedWork.Claim.ClaimId,
                FailureId = claimedWork.Claim.FailureId,
                RuntimeInstanceId =
                    claimedWork.Claim.RuntimeInstanceId,
                CandidateCount = outcomes.Count,
                AcceptedCount =
                    outcomes.Count(
                        outcome =>
                            outcome.Transition.Accepted),
                ChangedCount =
                    outcomes.Count(
                        outcome =>
                            outcome.Transition.Changed),
                RejectedCount =
                    outcomes.Count(
                        outcome =>
                            !outcome.Transition.Accepted),
                CompletedAtUtc =
                    DateTimeOffset.UtcNow,
                Outcomes = outcomes
            };
        }

        /// <summary>
        /// Validates the acquired claim, lease, and exact inventory fingerprint.
        /// </summary>
        private static void ValidateClaimedAuthority(
            AiRuntimePoolClaimedAssignedWork claimedWork)
        {
            if (claimedWork.Status !=
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .ClaimNotAcquired,
                    "Claimed recovery execution requires an acquired claim.");
            }

            if (claimedWork.Lease is null)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseMissing,
                    "The acquired recovery claim does not include its lease.");
            }

            if (claimedWork.Lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseReleased,
                    "The recovery claim lease has already been released.");
            }

            if (!StringComparer.Ordinal.Equals(
                    claimedWork.Lease.Claim.ClaimId,
                    claimedWork.Claim.ClaimId))
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseClaimMismatch,
                    "The supplied recovery lease belongs to another claim.");
            }

            var inventory =
                claimedWork.Inventory;

            var claim =
                claimedWork.Claim;

            var authorityMatches =
                StringComparer.Ordinal.Equals(
                    claim.FailureId,
                    inventory.FailureId) &&
                StringComparer.Ordinal.Equals(
                    claim.PoolId,
                    inventory.PoolId) &&
                StringComparer.Ordinal.Equals(
                    claim.HostId,
                    inventory.HostId) &&
                StringComparer.Ordinal.Equals(
                    claim.RuntimeInstanceId,
                    inventory.RuntimeInstanceId) &&
                StringComparer.Ordinal.Equals(
                    claim.RouteId,
                    inventory.RouteId) &&
                claim.CandidateCount ==
                    inventory.Candidates.Count;

            if (!authorityMatches)
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InventoryAuthorityMismatch,
                    "The recovery claim and assigned-work inventory authority differ.");
            }

            var fingerprint =
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(inventory);

            if (!StringComparer.Ordinal.Equals(
                    claim.InventoryFingerprint,
                    fingerprint))
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InventoryFingerprintMismatch,
                    "The assigned-work inventory no longer matches the claimed fingerprint.");
            }
        }

        /// <summary>
        /// Validates one candidate remains inside the exact inventory authority.
        /// </summary>
        private static void ValidateCandidateAuthority(
            AiRuntimePoolAssignedWorkInventory inventory,
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var matches =
                StringComparer.Ordinal.Equals(
                    candidate.FailureId,
                    inventory.FailureId) &&
                StringComparer.Ordinal.Equals(
                    candidate.PoolId,
                    inventory.PoolId) &&
                StringComparer.Ordinal.Equals(
                    candidate.HostId,
                    inventory.HostId) &&
                StringComparer.Ordinal.Equals(
                    candidate.RuntimeInstanceId,
                    inventory.RuntimeInstanceId) &&
                StringComparer.Ordinal.Equals(
                    candidate.RouteId,
                    inventory.RouteId);

            if (!matches)
            {
                throw CreateAuthorityException(
                    inventory.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .CandidateBoundaryViolation,
                    $"Candidate '{candidate.LocalRunId}' escaped the exact failed-runtime inventory boundary.");
            }
        }

        /// <summary>
        /// Validates candidate-kind-specific durable identity.
        /// </summary>
        private static void ValidateCandidateKind(
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            if (candidate.Kind ==
                    AiRuntimePoolAssignedWorkKind.InFlight &&
                string.IsNullOrWhiteSpace(
                    candidate.ExecutionId))
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
                string.IsNullOrWhiteSpace(
                    candidate.SharedRunId))
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
                !string.IsNullOrWhiteSpace(
                    candidate.ExecutionId))
            {
                throw CreateAuthorityException(
                    candidate.FailureId,
                    candidate.LocalRunId,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .CandidateBoundaryViolation,
                    $"Local-queued candidate '{candidate.LocalRunId}' unexpectedly carries ExecutionId '{candidate.ExecutionId}'.");
            }
        }

        /// <summary>
        /// Validates ownership resolution cannot cross runtime, run, execution, tenant, or shared
        /// run boundaries.
        /// </summary>
        private static void ValidateOwnershipBoundary(
            string failureId,
            AiRuntimePoolAssignedWorkCandidate candidate,
            AiSharedRunOwnershipResolutionResult ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);

            var runtimeMatches =
                StringComparer.Ordinal.Equals(
                    ownership.RuntimeInstanceId,
                    candidate.RuntimeInstanceId);

            var localRunMatches =
                string.IsNullOrWhiteSpace(
                    ownership.LocalRunId) ||
                StringComparer.Ordinal.Equals(
                    ownership.LocalRunId,
                    candidate.LocalRunId);

            var executionMatches =
                string.IsNullOrWhiteSpace(
                    ownership.ExecutionId) ||
                StringComparer.Ordinal.Equals(
                    ownership.ExecutionId,
                    candidate.ExecutionId);

            var tenantMatches =
                string.IsNullOrWhiteSpace(
                    ownership.TenantId) ||
                StringComparer.Ordinal.Equals(
                    ownership.TenantId,
                    candidate.TenantId);

            var tenantGroupMatches =
                string.IsNullOrWhiteSpace(
                    ownership.TenantGroupId) ||
                StringComparer.Ordinal.Equals(
                    ownership.TenantGroupId,
                    candidate.TenantGroupId);

            var sharedRunMatches =
                string.IsNullOrWhiteSpace(
                    ownership.SharedRunId) ||
                string.IsNullOrWhiteSpace(
                    candidate.SharedRunId) ||
                StringComparer.Ordinal.Equals(
                    ownership.SharedRunId,
                    candidate.SharedRunId);

            if (!runtimeMatches ||
                !localRunMatches ||
                !executionMatches ||
                !tenantMatches ||
                !tenantGroupMatches ||
                !sharedRunMatches)
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
                var resolvedIdentityComplete =
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
                    (
                        string.IsNullOrWhiteSpace(
                            candidate.SharedRunId) ||
                        StringComparer.Ordinal.Equals(
                            ownership.SharedRunId,
                            candidate.SharedRunId)
                    );

                if (!resolvedIdentityComplete)
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

        /// <summary>
        /// Validates the existing transition service reports the exact candidate identity.
        /// </summary>
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
                (
                    string.IsNullOrWhiteSpace(
                        transition.LocalRunId) ||
                    StringComparer.Ordinal.Equals(
                        transition.LocalRunId,
                        candidate.LocalRunId)
                ) &&
                (
                    string.IsNullOrWhiteSpace(
                        transition.ExecutionId) ||
                    StringComparer.Ordinal.Equals(
                        transition.ExecutionId,
                        candidate.ExecutionId)
                ) &&
                (
                    string.IsNullOrWhiteSpace(
                        transition.SharedRunId) ||
                    string.IsNullOrWhiteSpace(
                        candidate.SharedRunId) ||
                    StringComparer.Ordinal.Equals(
                        transition.SharedRunId,
                        candidate.SharedRunId)
                );

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

        /// <summary>
        /// Verifies the exact public lease incarnation remains active in the claim store.
        /// </summary>
        private async Task EnsureActiveLeaseAsync(
            AiRuntimePoolClaimedAssignedWork claimedWork,
            CancellationToken cancellationToken)
        {
            var lease =
                claimedWork.Lease
                ?? throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseMissing,
                    "The acquired recovery claim does not include its lease.");

            if (lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseReleased,
                    "The recovery claim lease was released during execution.");
            }

            var active =
                await this.claimStore
                    .IsActiveLeaseAsync(
                        claimedWork.Claim.FailureId,
                        claimedWork.Claim.ClaimId,
                        lease.LeaseId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!active)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    localRunId: null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .ClaimNotActive,
                    "The supplied recovery lease incarnation is no longer active.");
            }
        }

        /// <summary>
        /// Creates a deterministic no-mutation outcome for unsupported recoverable states.
        /// </summary>
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
                        SharedRunId =
                            candidate.SharedRunId,
                        RuntimeInstanceId =
                            candidate.RuntimeInstanceId,
                        LocalRunId =
                            candidate.LocalRunId,
                        ExecutionId =
                            candidate.ExecutionId,
                        Action = "none",
                        Reason =
                            UnsupportedCandidateReason
                    },
                CompletedAtUtc =
                    DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates one typed claimed-recovery authority exception.
        /// </summary>
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
