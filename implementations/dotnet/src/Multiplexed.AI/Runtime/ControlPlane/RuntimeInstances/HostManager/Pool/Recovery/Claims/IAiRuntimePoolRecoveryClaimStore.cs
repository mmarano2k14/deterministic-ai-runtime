using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Atomically owns one active recovery claim per exact failure authority.
    /// </summary>
    public interface IAiRuntimePoolRecoveryClaimStore
    {
        /// <summary>
        /// Attempts to acquire the only active claim for one exact failure inventory.
        /// </summary>
        Task<AiRuntimePoolRecoveryClaimAcquisition> TryAcquireAsync(
            AiRuntimePoolRecoveryClaimRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the active claim for one failure identifier, when present.
        /// </summary>
        Task<AiRuntimePoolRecoveryClaim?> GetByFailureIdAsync(
            string failureId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Determines whether one exact lease incarnation still owns the active claim.
        /// </summary>
        Task<bool> IsActiveLeaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            CancellationToken cancellationToken = default);
    }
}
