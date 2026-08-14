using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Enumerates all durable recoverable work assigned to one failed Pod membership.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodAssignedWorkEnumerator
    {
        /// <summary>
        /// Enumerates all and only work owned by the exact atomically suppressed Pod children.
        /// </summary>
        Task<AiKubernetesRuntimePoolPodAssignedWorkInventory> EnumerateAsync(
            AiKubernetesRuntimePoolPodAssignedWorkRequest request,
            CancellationToken cancellationToken = default);
    }
}
