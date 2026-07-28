using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Grpc.RuntimePool
{
    /// <summary>
    /// Sends exact runtime-instance commands through one stable Runtime Pool gRPC endpoint.
    /// </summary>
    public static class GrpcRuntimePoolCommandClient
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

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
                AiRuntimeInstanceCommandGrpc
                    .AiRuntimeInstanceCommandGrpcClient client,
                string runtimeInstanceId,
                string requestedBy,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

            var command =
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
                                "mcp-grpc-runtime-pool-proof"
                        }
                };

            using var call =
                client.ExecuteCommandAsync(
                    new AiRuntimeInstanceGrpcCommandRequest
                    {
                        RequestJson =
                            JsonSerializer.Serialize(
                                command,
                                JsonOptions)
                    },
                    cancellationToken:
                        cancellationToken);

            var response =
                await call.ResponseAsync
                    .ConfigureAwait(false);

            return JsonSerializer.Deserialize<
                    AiRuntimeInstanceCommandResult>(
                    response.ResponseJson,
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "The stable Runtime Pool gRPC endpoint returned an empty command result.");
        }
    }
}
