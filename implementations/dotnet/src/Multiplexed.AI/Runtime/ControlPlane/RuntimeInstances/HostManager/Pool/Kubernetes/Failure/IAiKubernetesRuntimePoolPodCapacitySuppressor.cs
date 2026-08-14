using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Atomically suppresses every current child capacity identity owned by one Pod UID.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodCapacitySuppressor
    {
        /// <summary>
        /// Enumerates exact membership and makes the full set unsafe before returning.
        /// </summary>
        Task<AiKubernetesRuntimePoolPodCapacitySuppression> SuppressAsync(
            AiKubernetesRuntimePoolPodCapacitySuppressionRequest request,
            CancellationToken cancellationToken = default);
    }
}
