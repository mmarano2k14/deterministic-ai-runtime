using System;
using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Maps the stable Runtime Pool gRPC command service.
    /// </summary>
    public static class AiRuntimePoolGrpcCommandEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps the stable Runtime Pool gRPC command service.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <returns>The gRPC service endpoint convention builder.</returns>
        public static GrpcServiceEndpointConventionBuilder
            MapAiRuntimePoolGrpcCommandService(
                this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            return endpoints
                .MapGrpcService<
                    AiRuntimePoolGrpcCommandService>();
        }
    }
}
