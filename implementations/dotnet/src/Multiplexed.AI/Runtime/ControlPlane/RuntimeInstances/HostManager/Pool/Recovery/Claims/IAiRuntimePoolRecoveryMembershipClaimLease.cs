using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Owns the only active lease incarnation for one membership recovery claim.
    /// </summary>
    public interface IAiRuntimePoolRecoveryMembershipClaimLease :
        IAsyncDisposable
    {
        /// <summary>
        /// Gets the membership claim owned by this lease.
        /// </summary>
        AiRuntimePoolRecoveryMembershipClaim Claim { get; }

        /// <summary>
        /// Gets the public lease incarnation identifier.
        /// </summary>
        string LeaseId { get; }

        /// <summary>
        /// Gets whether this lease has been released.
        /// </summary>
        bool IsReleased { get; }
    }
}
