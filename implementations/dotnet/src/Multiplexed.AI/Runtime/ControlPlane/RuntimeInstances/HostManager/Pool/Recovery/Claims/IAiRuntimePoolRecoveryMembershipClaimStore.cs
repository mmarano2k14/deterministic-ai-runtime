using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Atomically owns one active recovery claim for an exact failed runtime membership.
    /// </summary>
    public interface IAiRuntimePoolRecoveryMembershipClaimStore
    {
        /// <summary>
        /// Attempts to acquire the only active claim for one exact membership inventory.
        /// </summary>
        Task<AiRuntimePoolRecoveryMembershipClaimAcquisition>
            TryAcquireMembershipAsync(
                AiRuntimePoolRecoveryMembershipClaimRequest request,
                CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the active membership claim for one failure identifier, when present.
        /// </summary>
        Task<AiRuntimePoolRecoveryMembershipClaim?>
            GetMembershipByFailureIdAsync(
                string failureId,
                CancellationToken cancellationToken = default);

        /// <summary>
        /// Determines whether one exact membership lease incarnation remains active.
        /// </summary>
        Task<bool> IsActiveMembershipLeaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            CancellationToken cancellationToken = default);
    }
}
