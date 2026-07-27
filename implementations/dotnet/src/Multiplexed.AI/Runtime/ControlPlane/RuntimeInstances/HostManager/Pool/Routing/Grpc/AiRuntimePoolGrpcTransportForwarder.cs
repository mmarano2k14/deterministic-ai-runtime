using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Forwards existing command envelopes to exact child gRPC services.
    /// </summary>
    public sealed class AiRuntimePoolGrpcTransportForwarder :
        IAiRuntimePoolGrpcTransportForwarder
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IAiRuntimePoolGrpcClientFactory clientFactory;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolGrpcTransportForwarder"/> class.
        /// </summary>
        /// <param name="clientFactory">The exact child gRPC client factory.</param>
        public AiRuntimePoolGrpcTransportForwarder(
            IAiRuntimePoolGrpcClientFactory clientFactory)
        {
            this.clientFactory =
                clientFactory
                ?? throw new ArgumentNullException(
                    nameof(clientFactory));
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
                    "grpc"))
            {
                throw new InvalidOperationException(
                    $"Route '{route.RouteId}' does not use the gRPC transport.");
            }

            var targetRuntimeInstanceId =
                ResolveTargetRuntimeInstanceId(request);

            if (!StringComparer.Ordinal.Equals(
                    route.RuntimeInstanceId,
                    targetRuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    "The command target does not match the exact acquired gRPC route.");
            }

            await using var client =
                this.clientFactory.Create(
                    route.TransportEndpoint);

            var response =
                await client
                    .ExecuteCommandAsync(
                        new AiRuntimeInstanceGrpcCommandRequest
                        {
                            RequestJson =
                                JsonSerializer.Serialize(
                                    request,
                                    JsonOptions)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (response is null ||
                string.IsNullOrWhiteSpace(
                    response.ResponseJson))
            {
                throw new InvalidOperationException(
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .ResponseJsonMissing);
            }

            AiRuntimeInstanceCommandResult? result;

            try
            {
                result =
                    JsonSerializer.Deserialize<
                        AiRuntimeInstanceCommandResult>(
                        response.ResponseJson,
                        JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .ResponseJsonInvalid,
                    exception);
            }

            if (result is null)
            {
                throw new InvalidOperationException(
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .ResponseJsonInvalid);
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
