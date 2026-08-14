using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Acquires one deterministic and deduplicated recovery claim for an exact failed Pod inventory.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodRecoveryClaimCoordinator
    {
        /// <summary>
        /// Enumerates the current exact failed-Pod inventory and attempts to own its only claim.
        /// </summary>
        Task<AiKubernetesRuntimePoolPodClaimedAssignedWork> TryAcquireAsync(
            AiKubernetesRuntimePoolPodAssignedWorkRequest request,
            string claimedBy,
            CancellationToken cancellationToken = default);
    }
}
