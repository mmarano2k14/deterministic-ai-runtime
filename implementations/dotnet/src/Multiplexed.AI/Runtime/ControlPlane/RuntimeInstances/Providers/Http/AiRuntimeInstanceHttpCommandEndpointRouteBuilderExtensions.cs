using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Provides endpoint mapping extensions for runtime instance HTTP commands.
    /// </summary>
    public static class AiRuntimeInstanceHttpCommandEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Gets the default runtime instance command endpoint path.
        /// </summary>
        public const string DefaultCommandEndpointPath =
            AiRuntimeInstanceCommandTransportDefaults.DefaultHttpCommandEndpointPath;

        /// <summary>
        /// Maps the runtime instance HTTP command endpoint.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The endpoint pattern.</param>
        /// <returns>The route handler builder.</returns>
        public static RouteHandlerBuilder MapAiRuntimeInstanceHttpCommandEndpoint(
            this IEndpointRouteBuilder endpoints,
            string pattern = DefaultCommandEndpointPath)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

            return endpoints.MapPost(
                pattern,
                async (
                    [FromBody] AiRuntimeInstanceCommandRequest request,
                    [FromServices] IAiRuntimeInstanceHttpCommandHandler handler,
                    CancellationToken cancellationToken) =>
                {
                    ArgumentNullException.ThrowIfNull(request);
                    ArgumentNullException.ThrowIfNull(handler);

                    var result =
                        await handler
                            .HandleAsync(
                                request,
                                cancellationToken)
                            .ConfigureAwait(false);

                    return Results.Json(result);
                });
        }
    }
}