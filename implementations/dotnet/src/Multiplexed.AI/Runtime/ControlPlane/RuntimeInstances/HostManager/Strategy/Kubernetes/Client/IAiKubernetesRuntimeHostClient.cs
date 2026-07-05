using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Defines the Kubernetes runtime host lifecycle boundary used by the Kubernetes host creation strategy.
    /// </summary>
    /// <remarks>
    /// This abstraction isolates the runtime host strategy from the Kubernetes .NET SDK.
    /// Implementations may use an in-memory fake for tests or the real Kubernetes client for cluster execution.
    /// </remarks>
    public interface IAiKubernetesRuntimeHostClient
    {
        /// <summary>
        /// Creates the Kubernetes resources required to host a runtime instance.
        /// </summary>
        /// <param name="podSpec">The runtime-owned Kubernetes pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Kubernetes runtime host creation result.</returns>
        Task<AiKubernetesRuntimeHostCreateResult> CreateRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Waits until the Kubernetes host reaches Kubernetes-level readiness.
        /// </summary>
        /// <remarks>
        /// Kubernetes readiness does not replace runtime readiness.
        /// Runtime readiness must still be validated through registry, capacity, and tenant visibility.
        /// </remarks>
        /// <param name="podSpec">The runtime-owned Kubernetes pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Kubernetes runtime host readiness result.</returns>
        Task<AiKubernetesRuntimeHostReadinessResult> WaitUntilHostReadyAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes Kubernetes resources associated with a runtime host.
        /// </summary>
        /// <param name="podSpec">The runtime-owned Kubernetes pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Kubernetes runtime host delete result.</returns>
        Task<AiKubernetesRuntimeHostDeleteResult> DeleteRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default);
    }
}