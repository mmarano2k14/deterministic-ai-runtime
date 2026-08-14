using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Represents one exact failed-Pod inventory and its atomic recovery-claim result.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodClaimedAssignedWork
    {
        /// <summary>
        /// Gets the exact read-only Pod-wide assigned-work inventory.
        /// </summary>
        public required AiKubernetesRuntimePoolPodAssignedWorkInventory
            Inventory { get; init; }

        /// <summary>
        /// Gets the immutable Kubernetes Pod UID covered by this claim.
        /// </summary>
        public string PodUid => this.Inventory.PodUid;

        /// <summary>
        /// Gets the authoritative host incarnation identifier.
        /// </summary>
        public string HostId => this.Claim.HostId;

        /// <summary>
        /// Gets the claim acquisition status.
        /// </summary>
        public AiRuntimePoolRecoveryClaimAcquisitionStatus Status { get; init; }

        /// <summary>
        /// Gets the authoritative active membership claim.
        /// </summary>
        public required AiRuntimePoolRecoveryMembershipClaim Claim { get; init; }

        /// <summary>
        /// Gets the only active lease when the claim was acquired.
        /// </summary>
        public IAiRuntimePoolRecoveryMembershipClaimLease? Lease { get; init; }
    }
}
