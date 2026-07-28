using System;
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
    /// Executes one exact runtime claim through the shared candidate transition executor.
    /// </summary>
    public sealed class AiRuntimePoolClaimedRecoveryExecutor :
        IAiRuntimePoolClaimedRecoveryExecutor
    {
        private readonly IAiRuntimePoolRecoveryClaimStore claimStore;
        private readonly IAiRuntimePoolRecoveryCandidateTransitionExecutor
            candidateExecutor;

        /// <summary>
        /// Preserves the existing public composition while delegating candidate transitions to the
        /// shared executor used by runtime and Pod recovery.
        /// </summary>
        public AiRuntimePoolClaimedRecoveryExecutor(
            IAiRuntimePoolRecoveryClaimStore claimStore,
            IAiSharedRunOwnershipResolver ownershipResolver,
            IAiRuntimeExecutionRecoveryTransitionService transitionService)
            : this(
                claimStore,
                new AiRuntimePoolRecoveryCandidateTransitionExecutor(
                    ownershipResolver,
                    transitionService))
        {
        }

        public AiRuntimePoolClaimedRecoveryExecutor(
            IAiRuntimePoolRecoveryClaimStore claimStore,
            IAiRuntimePoolRecoveryCandidateTransitionExecutor candidateExecutor)
        {
            this.claimStore =
                claimStore
                ?? throw new ArgumentNullException(nameof(claimStore));
            this.candidateExecutor =
                candidateExecutor
                ?? throw new ArgumentNullException(nameof(candidateExecutor));
        }

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

            var inventory = claimedWork.Inventory;
            var outcomes =
                await this.candidateExecutor
                    .ExecuteAsync(
                        claimedWork.Claim.FailureId,
                        inventory.Candidates,
                        candidate => CandidateBelongsToInventory(
                            inventory,
                            candidate),
                        token => this.EnsureActiveLeaseAsync(
                            claimedWork,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiRuntimePoolClaimedRecoveryExecutionResult
            {
                ClaimId = claimedWork.Claim.ClaimId,
                FailureId = claimedWork.Claim.FailureId,
                RuntimeInstanceId =
                    claimedWork.Claim.RuntimeInstanceId,
                CandidateCount = outcomes.Count,
                AcceptedCount =
                    outcomes.Count(
                        outcome => outcome.Transition.Accepted),
                ChangedCount =
                    outcomes.Count(
                        outcome => outcome.Transition.Changed),
                RejectedCount =
                    outcomes.Count(
                        outcome => !outcome.Transition.Accepted),
                CompletedAtUtc = DateTimeOffset.UtcNow,
                Outcomes = outcomes
            };
        }

        private static void ValidateClaimedAuthority(
            AiRuntimePoolClaimedAssignedWork claimedWork)
        {
            if (claimedWork.Status !=
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .ClaimNotAcquired,
                    "Claimed recovery execution requires an acquired claim.");
            }

            if (claimedWork.Lease is null)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseMissing,
                    "The acquired recovery claim does not include its lease.");
            }

            if (claimedWork.Lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
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
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseClaimMismatch,
                    "The supplied recovery lease belongs to another claim.");
            }

            var inventory = claimedWork.Inventory;
            var claim = claimedWork.Claim;

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
                claim.CandidateCount == inventory.Candidates.Count;

            if (!authorityMatches)
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    null,
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
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InventoryFingerprintMismatch,
                    "The assigned-work inventory no longer matches the claimed fingerprint.");
            }
        }

        private static bool CandidateBelongsToInventory(
            AiRuntimePoolAssignedWorkInventory inventory,
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            return
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
        }

        private async Task EnsureActiveLeaseAsync(
            AiRuntimePoolClaimedAssignedWork claimedWork,
            CancellationToken cancellationToken)
        {
            var lease =
                claimedWork.Lease
                ?? throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseMissing,
                    "The acquired recovery claim does not include its lease.");

            if (lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
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
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .ClaimNotActive,
                    "The supplied recovery lease incarnation is no longer active.");
            }
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
