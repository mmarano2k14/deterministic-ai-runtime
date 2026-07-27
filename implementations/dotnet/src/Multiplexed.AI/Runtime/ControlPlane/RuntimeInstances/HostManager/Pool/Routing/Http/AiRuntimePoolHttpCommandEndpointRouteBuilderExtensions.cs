using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Maps the stable Runtime Pool HTTP command endpoint.
    /// </summary>
    public static class AiRuntimePoolHttpCommandEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Gets the stable Runtime Pool HTTP command path.
        /// </summary>
        public const string DefaultCommandEndpointPath =
            "/runtime-pool/commands";

        /// <summary>
        /// Maps the stable Runtime Pool HTTP command endpoint.
        /// </summary>
        /// <param name="endpoints">The endpoint route builder.</param>
        /// <param name="pattern">The stable endpoint pattern.</param>
        /// <returns>The route handler builder.</returns>
        public static RouteHandlerBuilder
            MapAiRuntimePoolHttpCommandEndpoint(
                this IEndpointRouteBuilder endpoints,
                string pattern = DefaultCommandEndpointPath)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

            return endpoints.MapPost(
                pattern,
                async (
                    [FromBody]
                    AiRuntimeInstanceCommandRequest request,
                    [FromServices]
                    IAiRuntimePoolHttpCommandHandler handler,
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
