using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Forwards existing runtime commands to exact HTTP child endpoints.
    /// </summary>
    public sealed class AiRuntimePoolHttpTransportForwarder :
        IAiRuntimePoolHttpTransportForwarder
    {
        /// <summary>
        /// Gets the named HTTP client used by process-pool routing.
        /// </summary>
        public const string HttpClientName =
            "Multiplexed.AI.RuntimePool.HttpForwarder";

        /// <summary>
        /// Gets the existing child runtime HTTP command path.
        /// </summary>
        public const string ChildCommandEndpointPath =
            "/runtime-instance/commands";

        private readonly HttpClient httpClient;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolHttpTransportForwarder"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        public AiRuntimePoolHttpTransportForwarder(
            HttpClient httpClient)
        {
            this.httpClient =
                httpClient
                ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCommandResult> ForwardAsync(
            AiRuntimePoolRouteDescriptor route,
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(route);
            ArgumentNullException.ThrowIfNull(request);

            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    route.TransportName,
                    "http"))
            {
                throw new InvalidOperationException(
                    $"Route '{route.RouteId}' does not use the HTTP transport.");
            }

            var targetRuntimeInstanceId =
                ResolveTargetRuntimeInstanceId(request);

            if (!StringComparer.Ordinal.Equals(
                    route.RuntimeInstanceId,
                    targetRuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    "The command target does not match the exact acquired route.");
            }

            var commandUri =
                BuildCommandUri(
                    route.TransportEndpoint);

            using var response =
                await this.httpClient
                    .PostAsJsonAsync(
                        commandUri,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var result =
                await response.Content
                    .ReadFromJsonAsync<AiRuntimeInstanceCommandResult>(
                        cancellationToken)
                    .ConfigureAwait(false);

            if (result is null)
            {
                throw new InvalidOperationException(
                    $"Runtime route '{route.RouteId}' returned an empty HTTP command result.");
            }

            if (!StringComparer.Ordinal.Equals(
                    result.RuntimeInstanceId,
                    route.RuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    $"Runtime route '{route.RouteId}' returned result identity '{result.RuntimeInstanceId}' instead of '{route.RuntimeInstanceId}'.");
            }

            return result;
        }

        /// <summary>
        /// Builds the exact child command URI from the registered base endpoint.
        /// </summary>
        /// <param name="transportEndpoint">The registered child transport endpoint.</param>
        /// <returns>The absolute child command URI.</returns>
        internal static Uri BuildCommandUri(
            string transportEndpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                transportEndpoint);

            if (!Uri.TryCreate(
                    transportEndpoint.Trim(),
                    UriKind.Absolute,
                    out var baseUri))
            {
                throw new ArgumentException(
                    "The route transport endpoint must be an absolute URI.",
                    nameof(transportEndpoint));
            }

            var normalizedBase =
                new Uri(
                    string.Concat(
                        baseUri
                            .GetLeftPart(UriPartial.Authority)
                            .TrimEnd('/'),
                        "/"));

            return new Uri(
                normalizedBase,
                ChildCommandEndpointPath.TrimStart('/'));
        }

        /// <summary>
        /// Resolves the exact target identity carried by the existing command request.
        /// </summary>
        private static string ResolveTargetRuntimeInstanceId(
            AiRuntimeInstanceCommandRequest request)
        {
            var runtimeInstanceId =
                request.DispatchRequest?.RuntimeInstanceId ??
                request.RuntimeInstanceId;

            ArgumentException.ThrowIfNullOrWhiteSpace(
                runtimeInstanceId);

            return runtimeInstanceId.Trim();
        }
    }
}
