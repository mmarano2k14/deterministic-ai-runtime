using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Places one atomic deterministic recovery claim around an exact failed-Pod inventory.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolPodRecoveryClaimCoordinator :
        IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator
    {
        private readonly IAiKubernetesRuntimePoolPodAssignedWorkEnumerator
            assignedWorkEnumerator;

        private readonly IAiRuntimePoolRecoveryMembershipClaimStore claimStore;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiKubernetesRuntimePoolPodRecoveryClaimCoordinator"/> class.
        /// </summary>
        public AiKubernetesRuntimePoolPodRecoveryClaimCoordinator(
            IAiKubernetesRuntimePoolPodAssignedWorkEnumerator
                assignedWorkEnumerator,
            IAiRuntimePoolRecoveryMembershipClaimStore claimStore)
        {
            this.assignedWorkEnumerator =
                assignedWorkEnumerator
                ?? throw new ArgumentNullException(
                    nameof(assignedWorkEnumerator));

            this.claimStore =
                claimStore
                ?? throw new ArgumentNullException(nameof(claimStore));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimePoolPodClaimedAssignedWork>
            TryAcquireAsync(
                AiKubernetesRuntimePoolPodAssignedWorkRequest request,
                string claimedBy,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);
            cancellationToken.ThrowIfCancellationRequested();

            var inventory =
                await this.assignedWorkEnumerator
                    .EnumerateAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            ValidateInventory(request, inventory);

            var acquisition =
                await this.claimStore
                    .TryAcquireMembershipAsync(
                        new AiRuntimePoolRecoveryMembershipClaimRequest
                        {
                            FailureId = inventory.FailureId,
                            PoolId = inventory.PoolId,
                            HostId = inventory.PodUid,
                            MembershipFingerprint =
                                AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                                    .CalculateMembership(inventory),
                            MemberCount =
                                inventory.RuntimeInventories.Count,
                            InventoryFingerprint =
                                AiKubernetesRuntimePoolPodRecoveryInventoryFingerprint
                                    .CalculateInventory(inventory),
                            CandidateCount =
                                inventory.Candidates.Count,
                            ClaimedBy = claimedBy.Trim()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiKubernetesRuntimePoolPodClaimedAssignedWork
            {
                Inventory = inventory,
                Status = acquisition.Status,
                Claim = acquisition.Claim,
                Lease = acquisition.Lease
            };
        }

        private static void ValidateInventory(
            AiKubernetesRuntimePoolPodAssignedWorkRequest request,
            AiKubernetesRuntimePoolPodAssignedWorkInventory inventory)
        {
            ArgumentNullException.ThrowIfNull(inventory);

            var requestMatches =
                StringComparer.Ordinal.Equals(
                    request.FailureId.Trim(),
                    inventory.FailureId) &&
                StringComparer.Ordinal.Equals(
                    request.PoolId.Trim(),
                    inventory.PoolId) &&
                StringComparer.Ordinal.Equals(
                    request.PodUid.Trim(),
                    inventory.PodUid);

            if (!requestMatches)
            {
                throw new InvalidOperationException(
                    "The failed-Pod assigned-work inventory does not match the requested recovery authority.");
            }

            if (inventory.RuntimeInventories.Count == 0)
            {
                throw new InvalidOperationException(
                    "A failed-Pod recovery claim requires at least one exact runtime member.");
            }

            if (inventory.RuntimeInventories.Any(
                    runtimeInventory =>
                        !StringComparer.Ordinal.Equals(
                            runtimeInventory.FailureId,
                            inventory.FailureId) ||
                        !StringComparer.Ordinal.Equals(
                            runtimeInventory.PoolId,
                            inventory.PoolId) ||
                        !StringComparer.Ordinal.Equals(
                            runtimeInventory.HostId,
                            inventory.PodUid)))
            {
                throw new InvalidOperationException(
                    "The failed-Pod inventory contains a runtime outside its exact failure, pool, or Pod UID boundary.");
            }

            var expectedCandidates =
                inventory.RuntimeInventories
                    .SelectMany(
                        runtimeInventory =>
                            runtimeInventory.Candidates)
                    .OrderBy(candidate => candidate.Kind)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate => candidate.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        candidate => candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            if (expectedCandidates.Length != inventory.Candidates.Count ||
                expectedCandidates
                    .Zip(
                        inventory.Candidates,
                        CandidateAuthorityMatches)
                    .Any(matches => !matches))
            {
                throw new InvalidOperationException(
                    "The failed-Pod aggregate candidates do not equal its exact deterministic per-runtime inventories.");
            }
        }

        private static bool CandidateAuthorityMatches(
            AiRuntimePoolAssignedWorkCandidate expected,
            AiRuntimePoolAssignedWorkCandidate actual)
        {
            return
                StringComparer.Ordinal.Equals(
                    expected.FailureId,
                    actual.FailureId) &&
                StringComparer.Ordinal.Equals(
                    expected.PoolId,
                    actual.PoolId) &&
                StringComparer.Ordinal.Equals(
                    expected.HostId,
                    actual.HostId) &&
                StringComparer.Ordinal.Equals(
                    expected.RuntimeInstanceId,
                    actual.RuntimeInstanceId) &&
                StringComparer.Ordinal.Equals(
                    expected.RouteId,
                    actual.RouteId) &&
                StringComparer.Ordinal.Equals(
                    expected.LocalRunId,
                    actual.LocalRunId) &&
                StringComparer.Ordinal.Equals(
                    expected.ExecutionId,
                    actual.ExecutionId) &&
                expected.Kind == actual.Kind &&
                expected.CreatedAtUtc == actual.CreatedAtUtc;
        }
    }
}
