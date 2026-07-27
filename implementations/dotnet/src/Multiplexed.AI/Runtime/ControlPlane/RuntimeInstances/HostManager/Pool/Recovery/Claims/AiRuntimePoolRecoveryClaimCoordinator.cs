using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Places one atomic deterministic recovery claim around an exact assigned-work inventory.
    /// </summary>
    public sealed class AiRuntimePoolRecoveryClaimCoordinator :
        IAiRuntimePoolRecoveryClaimCoordinator
    {
        private readonly IAiRuntimePoolAssignedWorkEnumerator
            assignedWorkEnumerator;

        private readonly IAiRuntimePoolRecoveryClaimStore claimStore;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolRecoveryClaimCoordinator"/> class.
        /// </summary>
        /// <param name="assignedWorkEnumerator">The exact assigned-work enumerator.</param>
        /// <param name="claimStore">The atomic claim store.</param>
        public AiRuntimePoolRecoveryClaimCoordinator(
            IAiRuntimePoolAssignedWorkEnumerator assignedWorkEnumerator,
            IAiRuntimePoolRecoveryClaimStore claimStore)
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
        public async Task<AiRuntimePoolClaimedAssignedWork>
            TryAcquireAsync(
                string failureId,
                string claimedBy,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

            var inventory =
                await this.assignedWorkEnumerator
                    .EnumerateAsync(
                        failureId.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);

            var acquisition =
                await this.claimStore
                    .TryAcquireAsync(
                        new AiRuntimePoolRecoveryClaimRequest
                        {
                            FailureId =
                                inventory.FailureId,
                            PoolId = inventory.PoolId,
                            HostId = inventory.HostId,
                            RuntimeInstanceId =
                                inventory.RuntimeInstanceId,
                            RouteId = inventory.RouteId,
                            InventoryFingerprint =
                                AiRuntimePoolRecoveryInventoryFingerprint
                                    .Calculate(inventory),
                            CandidateCount =
                                inventory.Candidates.Count,
                            ClaimedBy = claimedBy.Trim()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiRuntimePoolClaimedAssignedWork
            {
                Inventory = inventory,
                Status = acquisition.Status,
                Claim = acquisition.Claim,
                Lease = acquisition.Lease
            };
        }
    }
}
