using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client
{
    /// <summary>
    /// Defines the Kubernetes lifecycle boundary for one Runtime Pool Pod and stable Service.
    /// </summary>
    public interface IAiKubernetesRuntimePoolHostClient
    {
        /// <summary>
        /// Creates the Kubernetes resources for one Runtime Pool Pod.
        /// </summary>
        Task<AiKubernetesRuntimeHostCreateResult> CreateRuntimePoolHostAsync(
            AiKubernetesRuntimePoolPodSpec podSpec,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Waits for Kubernetes-level Pod and Service readiness.
        /// </summary>
        Task<AiKubernetesRuntimeHostReadinessResult> WaitUntilHostReadyAsync(
            AiKubernetesRuntimePoolPodSpec podSpec,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the Kubernetes resources for one Runtime Pool Pod.
        /// </summary>
        Task<AiKubernetesRuntimeHostDeleteResult> DeleteRuntimePoolHostAsync(
            AiKubernetesRuntimePoolPodSpec podSpec,
            CancellationToken cancellationToken = default);
    }
}
