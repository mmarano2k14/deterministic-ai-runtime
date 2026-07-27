using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents the only release authority for one active recovery claim.
    /// </summary>
    public interface IAiRuntimePoolRecoveryClaimLease :
        IAsyncDisposable
    {
        /// <summary>
        /// Gets the active recovery claim.
        /// </summary>
        AiRuntimePoolRecoveryClaim Claim { get; }

        /// <summary>
        /// Gets the unique active lease-incarnation identifier.
        /// </summary>
        string LeaseId { get; }

        /// <summary>
        /// Gets a value indicating whether this lease has been released.
        /// </summary>
        bool IsReleased { get; }
    }
}
