using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents one exact inventory and its atomic recovery-claim result.
    /// </summary>
    public sealed record AiRuntimePoolClaimedAssignedWork
    {
        /// <summary>
        /// Gets the exact read-only assigned-work inventory.
        /// </summary>
        public required AiRuntimePoolAssignedWorkInventory Inventory
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the claim acquisition status.
        /// </summary>
        public AiRuntimePoolRecoveryClaimAcquisitionStatus Status
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the authoritative active claim.
        /// </summary>
        public required AiRuntimePoolRecoveryClaim Claim { get; init; }

        /// <summary>
        /// Gets the only active lease when the claim was acquired.
        /// </summary>
        public IAiRuntimePoolRecoveryClaimLease? Lease { get; init; }
    }
}
