using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Creates and validates one ready replacement Pod under an active exact recovery lease.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodReplacementCoordinator
    {
        /// <summary>
        /// Creates replacement capacity through the existing KubernetesPool host strategy and
        /// validates that every shared-registry runtime incarnation is fresh.
        /// </summary>
        Task<AiKubernetesRuntimePoolPodReplacement> CreateReplacementAsync(
            AiKubernetesRuntimePoolPodReplacementRequest request,
            CancellationToken cancellationToken = default);
    }
}
