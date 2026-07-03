using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Provides endpoint mapping extensions for the gRPC runtime instance command service.
    /// </summary>
    public static class AiRuntimeInstanceGrpcCommandEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps the gRPC runtime instance command service.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <returns>The endpoint convention builder.</returns>
        public static GrpcServiceEndpointConventionBuilder MapAiRuntimeInstanceGrpcCommandService(
            this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            return endpoints.MapGrpcService<AiRuntimeInstanceGrpcCommandService>();
        }
    }
}