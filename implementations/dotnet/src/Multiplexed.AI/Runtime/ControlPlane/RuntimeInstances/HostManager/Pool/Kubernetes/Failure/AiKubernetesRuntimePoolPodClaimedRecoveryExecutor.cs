using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Executes one Pod membership claim through the same candidate transition core as process recovery.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodClaimedRecoveryExecutor :
        IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor
    {
        private readonly IAiRuntimePoolRecoveryMembershipClaimStore claimStore;
        private readonly IAiRuntimePoolRecoveryCandidateTransitionExecutor
            candidateExecutor;

        public AiKubernetesRuntimePoolPodClaimedRecoveryExecutor(
            IAiRuntimePoolRecoveryMembershipClaimStore claimStore,
            IAiRuntimePoolRecoveryCandidateTransitionExecutor candidateExecutor)
        {
            this.claimStore =
                claimStore
                ?? throw new ArgumentNullException(nameof(claimStore));
            this.candidateExecutor =
                candidateExecutor
                ?? throw new ArgumentNullException(nameof(candidateExecutor));
        }

        public async Task<AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult>
            ExecuteAsync(
                AiKubernetesRuntimePoolPodClaimedAssignedWork claimedWork,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(claimedWork);
            ValidateClaimedAuthority(claimedWork);

            await this.EnsureActiveLeaseAsync(
                    claimedWork,
                    cancellationToken)
                .ConfigureAwait(false);

            var inventory = claimedWork.Inventory;
            var exactMemberIds =
                inventory.RuntimeInventories
                    .Select(item => item.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            var outcomes =
                await this.candidateExecutor
                    .ExecuteAsync(
                        claimedWork.Claim.FailureId,
                        inventory.Candidates,
                        candidate => CandidateBelongsToInventory(
                            inventory,
                            exactMemberIds,
                            candidate),
                        token => this.EnsureActiveLeaseAsync(
                            claimedWork,
                            token),
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult
            {
                ClaimId = claimedWork.Claim.ClaimId,
                FailureId = claimedWork.Claim.FailureId,
                PoolId = claimedWork.Claim.PoolId,
                PodUid = claimedWork.Claim.HostId,
                MemberCount = inventory.RuntimeInventories.Count,
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
            AiKubernetesRuntimePoolPodClaimedAssignedWork claimedWork)
        {
            var claim = claimedWork.Claim;
            var lease = claimedWork.Lease;
            var inventory = claimedWork.Inventory;

            if (claimedWork.Status !=
                    AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired ||
                lease is null)
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .ClaimNotAcquired,
                    "Failed-Pod recovery execution requires an acquired membership claim.");
            }

            if (lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseReleased,
                    "The failed-Pod recovery membership lease has already been released.");
            }

            if (!ClaimsMatch(claim, lease.Claim))
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseClaimMismatch,
                    "The failed-Pod recovery lease belongs to another membership claim.");
            }

            var matches =
                StringComparer.Ordinal.Equals(
                    claim.FailureId,
                    inventory.FailureId) &&
                StringComparer.Ordinal.Equals(
                    claim.PoolId,
                    inventory.PoolId) &&
                StringComparer.Ordinal.Equals(
                    claim.HostId,
                    inventory.PodUid) &&
                claim.MemberCount == inventory.RuntimeInventories.Count &&
                claim.CandidateCount == inventory.Candidates.Count &&
                StringComparer.Ordinal.Equals(
                    claim.MembershipFingerprint,
                    AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                        .CalculateMembership(inventory)) &&
                StringComparer.Ordinal.Equals(
                    claim.InventoryFingerprint,
                    AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                        .CalculateInventory(inventory));

            if (!matches)
            {
                throw CreateAuthorityException(
                    claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .InventoryAuthorityMismatch,
                    "The failed-Pod inventory no longer matches its claimed membership and work fingerprints.");
            }
        }

        private static bool CandidateBelongsToInventory(
            AiKubernetesRuntimePoolPodAssignedWorkInventory inventory,
            IReadOnlySet<string> exactMemberIds,
            AiRuntimePoolAssignedWorkCandidate candidate)
        {
            return
                exactMemberIds.Contains(candidate.RuntimeInstanceId) &&
                StringComparer.Ordinal.Equals(
                    candidate.FailureId,
                    inventory.FailureId) &&
                StringComparer.Ordinal.Equals(
                    candidate.PoolId,
                    inventory.PoolId) &&
                StringComparer.Ordinal.Equals(
                    candidate.HostId,
                    inventory.PodUid) &&
                candidate.RouteId is null;
        }

        private async Task EnsureActiveLeaseAsync(
            AiKubernetesRuntimePoolPodClaimedAssignedWork claimedWork,
            CancellationToken cancellationToken)
        {
            var lease =
                claimedWork.Lease
                ?? throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseMissing,
                    "The failed-Pod recovery claim does not include its lease.");

            if (lease.IsReleased)
            {
                throw CreateAuthorityException(
                    claimedWork.Claim.FailureId,
                    null,
                    AiRuntimePoolRecoveryExecutionAuthorityFailure
                        .LeaseReleased,
                    "The failed-Pod recovery lease was released during execution.");
            }

            var active =
                await this.claimStore
                    .IsActiveMembershipLeaseAsync(
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
                    "The supplied failed-Pod recovery lease is no longer active.");
            }
        }

        private static bool ClaimsMatch(
            AiRuntimePoolRecoveryMembershipClaim first,
            AiRuntimePoolRecoveryMembershipClaim second)
        {
            return
                StringComparer.Ordinal.Equals(first.ClaimId, second.ClaimId) &&
                StringComparer.Ordinal.Equals(
                    first.FailureId,
                    second.FailureId) &&
                StringComparer.Ordinal.Equals(first.PoolId, second.PoolId) &&
                StringComparer.Ordinal.Equals(first.HostId, second.HostId) &&
                StringComparer.Ordinal.Equals(
                    first.MembershipFingerprint,
                    second.MembershipFingerprint) &&
                first.MemberCount == second.MemberCount &&
                StringComparer.Ordinal.Equals(
                    first.InventoryFingerprint,
                    second.InventoryFingerprint) &&
                first.CandidateCount == second.CandidateCount;
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
