using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Coordinates suppression, deduplicated recovery, and replacement for one deleted Pod.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator
    {
        Task<AiKubernetesRuntimePoolPodFailureRecoveryResult> RecoverAsync(
            AiKubernetesRuntimePoolPodFailureRecoveryRequest request,
            CancellationToken cancellationToken = default);
    }
}
