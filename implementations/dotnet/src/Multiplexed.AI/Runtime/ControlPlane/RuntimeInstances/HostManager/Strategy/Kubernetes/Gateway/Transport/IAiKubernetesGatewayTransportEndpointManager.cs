using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport
{
    /// <summary>
    /// Resolves the endpoint used by the control plane to reach the shared Kubernetes runtime Gateway.
    /// </summary>
    /// <remarks>
    /// In-cluster control planes receive the stable Gateway Service DNS endpoint.
    /// Local control planes can receive one process-shared kubectl port-forward endpoint.
    /// </remarks>
    public interface IAiKubernetesGatewayTransportEndpointManager : IDisposable
    {
        /// <summary>
        /// Resolves or creates the shared transport endpoint for a Kubernetes Gateway.
        /// </summary>
        /// <param name="gatewayEndpoint">The Gateway and its controller-managed Service.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The endpoint to publish to HTTP or gRPC runtime providers.</returns>
        Task<AiKubernetesGatewayTransportEndpoint> ResolveAsync(
            AiKubernetesGatewayEndpoint gatewayEndpoint,
            CancellationToken cancellationToken = default);
    }
}
