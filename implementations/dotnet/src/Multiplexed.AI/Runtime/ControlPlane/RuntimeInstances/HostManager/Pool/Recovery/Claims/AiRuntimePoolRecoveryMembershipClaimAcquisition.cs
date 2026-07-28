namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents the atomic acquisition result for one exact membership recovery claim.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryMembershipClaimAcquisition
    {
        /// <summary>
        /// Gets the claim acquisition status.
        /// </summary>
        public AiRuntimePoolRecoveryClaimAcquisitionStatus Status { get; init; }

        /// <summary>
        /// Gets the authoritative active membership claim.
        /// </summary>
        public required AiRuntimePoolRecoveryMembershipClaim Claim { get; init; }

        /// <summary>
        /// Gets the only active lease when acquisition succeeded.
        /// </summary>
        public IAiRuntimePoolRecoveryMembershipClaimLease? Lease { get; init; }
    }
}
