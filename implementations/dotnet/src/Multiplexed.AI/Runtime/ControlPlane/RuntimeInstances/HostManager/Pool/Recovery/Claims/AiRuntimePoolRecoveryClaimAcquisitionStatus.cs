namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Defines the outcome of one atomic recovery-claim acquisition attempt.
    /// </summary>
    public enum AiRuntimePoolRecoveryClaimAcquisitionStatus
    {
        /// <summary>
        /// The caller acquired the only active claim lease.
        /// </summary>
        Acquired = 0,

        /// <summary>
        /// Another lease already owns the exact failure recovery authority.
        /// </summary>
        AlreadyClaimed = 1
    }
}
