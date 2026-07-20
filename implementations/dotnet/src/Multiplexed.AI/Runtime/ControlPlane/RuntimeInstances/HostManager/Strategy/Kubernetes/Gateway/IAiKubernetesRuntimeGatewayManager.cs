using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Provides idempotent lifecycle operations for the shared Kubernetes runtime Gateway.
    /// </summary>
    /// <remarks>
    /// The Gateway controller and configured GatewayClass remain cluster prerequisites.
    /// This manager creates only the namespaced Gateway resource when it is missing.
    /// Runtime-specific HTTPRoute and GRPCRoute resources are handled separately.
    /// </remarks>
    public interface IAiKubernetesRuntimeGatewayManager
    {
        /// <summary>
        /// Ensures that the configured shared runtime Gateway exists, is accepted and programmed,
        /// and has a reachable Kubernetes Service backing it.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane requesting the shared Gateway.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved shared Gateway endpoint.</returns>
        Task<AiKubernetesGatewayEndpoint> EnsureGatewayAsync(
            string controlPlaneId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures that an HTTPRoute exists for one runtime instance, points to the expected
        /// runtime Service, and has been accepted with all backend references resolved.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane owning the runtime route.</param>
        /// <param name="runtimeInstanceId">The runtime instance selected by the routing header.</param>
        /// <param name="runtimeServiceName">The Kubernetes Service backing the runtime.</param>
        /// <param name="backendPort">The runtime Service backend port.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved HTTP runtime route.</returns>
        Task<AiKubernetesRuntimeRouteResult> EnsureHttpRouteAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures that a GRPCRoute exists for one runtime instance, points to the expected
        /// runtime Service, and has been accepted with all backend references resolved.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane owning the runtime route.</param>
        /// <param name="runtimeInstanceId">The runtime instance selected by the gRPC metadata header.</param>
        /// <param name="runtimeServiceName">The Kubernetes Service backing the runtime.</param>
        /// <param name="backendPort">The runtime Service backend port.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved gRPC runtime route.</returns>
        Task<AiKubernetesRuntimeRouteResult> EnsureGrpcRouteAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeServiceName,
            int backendPort,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes any HTTPRoute or GRPCRoute managed for one runtime instance.
        /// </summary>
        /// <remarks>
        /// The operation is idempotent. Missing routes are treated as already deleted.
        /// The shared Gateway and its backing Service are never deleted by this operation.
        /// </remarks>
        /// <param name="runtimeInstanceId">The runtime instance whose routes must be removed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        Task DeleteRuntimeRouteAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}
