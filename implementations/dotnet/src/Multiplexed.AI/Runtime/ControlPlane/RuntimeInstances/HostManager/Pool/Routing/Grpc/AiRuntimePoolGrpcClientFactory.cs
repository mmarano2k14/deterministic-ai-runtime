using System;
using Grpc.Net.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Creates real generated gRPC clients for exact RuntimeInstanceOnly child endpoints.
    /// </summary>
    public sealed class AiRuntimePoolGrpcClientFactory :
        IAiRuntimePoolGrpcClientFactory
    {
        /// <inheritdoc />
        public IAiRuntimePoolGrpcClient Create(
            string transportEndpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                transportEndpoint);

            if (!Uri.TryCreate(
                    transportEndpoint.Trim(),
                    UriKind.Absolute,
                    out var endpoint))
            {
                throw new ArgumentException(
                    "The gRPC route endpoint must be an absolute URI.",
                    nameof(transportEndpoint));
            }

            var channel =
                GrpcChannel.ForAddress(
                    endpoint);

            var client =
                new AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient(
                        channel);

            return new AiRuntimePoolGrpcClient(
                channel,
                client);
        }
    }
}
