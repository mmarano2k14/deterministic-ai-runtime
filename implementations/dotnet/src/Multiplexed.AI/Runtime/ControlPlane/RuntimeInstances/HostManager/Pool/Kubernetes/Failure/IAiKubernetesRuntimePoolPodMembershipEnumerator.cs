using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Enumerates the exact current runtime membership of one Kubernetes Runtime Pool Pod UID.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodMembershipEnumerator
    {
        /// <summary>
        /// Enumerates every current route incarnation owned by one exact Pod UID.
        /// </summary>
        /// <param name="poolId">The logical Runtime Pool identifier.</param>
        /// <param name="podUid">The immutable Kubernetes Pod UID.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative route-backed Pod membership snapshot.</returns>
        Task<AiKubernetesRuntimePoolPodMembership> EnumerateAsync(
            string poolId,
            string podUid,
            CancellationToken cancellationToken = default);
    }
}
