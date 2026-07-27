using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Enumerates exact assigned work and atomically claims its future recovery.
    /// </summary>
    public interface IAiRuntimePoolRecoveryClaimCoordinator
    {
        /// <summary>
        /// Attempts to claim the exact assigned-work inventory for one failure.
        /// </summary>
        /// <param name="failureId">The immutable failure observation identifier.</param>
        /// <param name="claimedBy">The recovery coordinator identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact inventory and atomic claim result.</returns>
        Task<AiRuntimePoolClaimedAssignedWork> TryAcquireAsync(
            string failureId,
            string claimedBy,
            CancellationToken cancellationToken = default);
    }
}
