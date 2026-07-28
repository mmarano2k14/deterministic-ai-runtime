using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http.RuntimePool
{
    /// <summary>
    /// Sends exact runtime-instance commands through one stable Runtime Pool HTTP endpoint.
    /// </summary>
    public static class HttpRuntimePoolCommandClient
    {
        /// <summary>
        /// Sends a queue-status command to one exact runtime instance.
        /// </summary>
        /// <param name="client">The client targeting the stable Runtime Pool endpoint.</param>
        /// <param name="runtimeInstanceId">The exact target runtime instance identifier.</param>
        /// <param name="requestedBy">The diagnostic caller identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact runtime command result.</returns>
        public static async Task<AiRuntimeInstanceCommandResult>
            GetQueueStatusAsync(
                HttpClient client,
                string runtimeInstanceId,
                string requestedBy,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

            using var response =
                await client
                    .PostAsJsonAsync(
                        AiRuntimePoolHttpCommandEndpointRouteBuilderExtensions
                            .DefaultCommandEndpointPath,
                        new AiRuntimeInstanceCommandRequest
                        {
                            Operation =
                                AiRuntimeInstanceCommandOperation
                                    .GetQueueStatus,
                            RuntimeInstanceId =
                                runtimeInstanceId,
                            QueueRequest =
                                new AiRuntimeQueueControlPlaneRequest
                                {
                                    Operation =
                                        AiRuntimeQueueControlPlaneOperation
                                            .GetQueueStatus,
                                    RuntimeInstanceId =
                                        runtimeInstanceId,
                                    CorrelationId =
                                        Guid.NewGuid()
                                            .ToString("N"),
                                    RequestedBy =
                                        requestedBy,
                                    Source =
                                        "mcp-http-runtime-pool-proof"
                                }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            return await response.Content
                .ReadFromJsonAsync<AiRuntimeInstanceCommandResult>(
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The stable Runtime Pool HTTP endpoint returned an empty command result.");
        }
    }
}
