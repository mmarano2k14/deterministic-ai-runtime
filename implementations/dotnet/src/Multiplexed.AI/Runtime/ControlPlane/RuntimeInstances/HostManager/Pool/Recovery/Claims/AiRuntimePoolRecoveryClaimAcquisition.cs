namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents one atomic recovery-claim acquisition result.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryClaimAcquisition
    {
        /// <summary>
        /// Gets the acquisition status.
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
        /// Gets the acquired lease when <see cref="Status"/> is
        /// <see cref="AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired"/>.
        /// </summary>
        public IAiRuntimePoolRecoveryClaimLease? Lease { get; init; }
    }
}
