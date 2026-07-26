using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Represents one exact child gRPC client and its owned channel.
    /// </summary>
    public interface IAiRuntimePoolGrpcClient :
        IAsyncDisposable
    {
        /// <summary>
        /// Executes one existing gRPC command envelope.
        /// </summary>
        /// <param name="request">The existing gRPC command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The existing gRPC command response.</returns>
        Task<AiRuntimeInstanceGrpcCommandResponse> ExecuteCommandAsync(
            AiRuntimeInstanceGrpcCommandRequest request,
            CancellationToken cancellationToken = default);
    }
}
