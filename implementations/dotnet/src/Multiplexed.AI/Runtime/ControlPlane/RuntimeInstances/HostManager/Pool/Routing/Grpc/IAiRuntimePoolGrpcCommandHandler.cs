using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Handles commands received by the stable Runtime Pool gRPC service.
    /// </summary>
    public interface IAiRuntimePoolGrpcCommandHandler
    {
        /// <summary>
        /// Routes one command to the exact target runtime instance.
        /// </summary>
        /// <param name="request">The existing runtime command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The existing runtime command result.</returns>
        Task<AiRuntimeInstanceCommandResult> HandleAsync(
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default);
    }
}
