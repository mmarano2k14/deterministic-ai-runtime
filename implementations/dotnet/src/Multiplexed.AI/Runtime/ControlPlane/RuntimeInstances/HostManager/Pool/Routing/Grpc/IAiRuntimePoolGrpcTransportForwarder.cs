using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Forwards one existing runtime command to one exact gRPC child endpoint.
    /// </summary>
    public interface IAiRuntimePoolGrpcTransportForwarder
    {
        /// <summary>
        /// Sends one command to the exact acquired route.
        /// </summary>
        /// <param name="route">The exact acquired route.</param>
        /// <param name="request">The existing runtime command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The existing runtime command result.</returns>
        Task<AiRuntimeInstanceCommandResult> ForwardAsync(
            AiRuntimePoolRouteDescriptor route,
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default);
    }
}
