using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Grpc.Core;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.Abstractions.AI.Observability;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Exposes one stable gRPC service for exact Runtime Pool command forwarding.
    /// </summary>
    public sealed class AiRuntimePoolGrpcCommandService :
        AiRuntimeInstanceCommandGrpc.AiRuntimeInstanceCommandGrpcBase
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IAiRuntimePoolGrpcCommandHandler handler;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolGrpcCommandService"/> class.
        /// </summary>
        /// <param name="handler">The exact pool command handler.</param>
        public AiRuntimePoolGrpcCommandService(
            IAiRuntimePoolGrpcCommandHandler handler)
        {
            this.handler =
                handler
                ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <inheritdoc />
        public override async Task<AiRuntimeInstanceGrpcCommandResponse>
            ExecuteCommand(
                AiRuntimeInstanceGrpcCommandRequest request,
                ServerCallContext context)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(context);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(
                    request.RequestJson))
            {
                return CreateResponse(
                    CreateTransportFailure(
                        startedAtUtc,
                        AiRuntimePoolGrpcRoutingFailureReasons
                            .RequestJsonMissing,
                        "The stable Runtime Pool gRPC request JSON is missing.",
                        exception: null));
            }

            AiRuntimeInstanceCommandRequest? commandRequest;

            try
            {
                commandRequest =
                    JsonSerializer.Deserialize<
                        AiRuntimeInstanceCommandRequest>(
                        request.RequestJson,
                        JsonOptions);
            }
            catch (JsonException exception)
            {
                return CreateResponse(
                    CreateTransportFailure(
                        startedAtUtc,
                        AiRuntimePoolGrpcRoutingFailureReasons
                            .RequestJsonInvalid,
                        exception.Message,
                        exception));
            }

            if (commandRequest is null)
            {
                return CreateResponse(
                    CreateTransportFailure(
                        startedAtUtc,
                        AiRuntimePoolGrpcRoutingFailureReasons
                            .RequestJsonInvalid,
                        "The stable Runtime Pool gRPC request JSON deserialized to null.",
                        exception: null));
            }

            var result =
                await this.handler
                    .HandleAsync(
                        commandRequest,
                        context.CancellationToken)
                    .ConfigureAwait(false);

            return CreateResponse(result);
        }

        /// <summary>
        /// Serializes one existing command result into the existing gRPC envelope.
        /// </summary>
        private static AiRuntimeInstanceGrpcCommandResponse
            CreateResponse(
                AiRuntimeInstanceCommandResult result)
        {
            return new AiRuntimeInstanceGrpcCommandResponse
            {
                ResponseJson =
                    JsonSerializer.Serialize(
                        result,
                        JsonOptions)
            };
        }

        /// <summary>
        /// Creates one explicit stable-service transport failure.
        /// </summary>
        private static AiRuntimeInstanceCommandResult
            CreateTransportFailure(
                DateTimeOffset startedAtUtc,
                string failureReason,
                string message,
                Exception? exception)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimePoolMetadataKeys.RoutingFailure] = "true",
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                        AiRuntimeInstanceCommandTransportMetadataKeys.GrpcTransportName
                };

            if (exception is not null)
            {
                metadata[AiExceptionMetadataKeys.ExceptionType] =
                    exception.GetType().FullName ??
                    exception.GetType().Name;
            }

            return new AiRuntimeInstanceCommandResult
            {
                Success = false,
                Operation = default,
                RuntimeInstanceId = string.Empty,
                Message = message,
                FailureReason = failureReason,
                StartedAtUtc = startedAtUtc,
                CompletedAtUtc = completedAtUtc,
                DurationMs =
                    Math.Max(
                        0,
                        (long)(completedAtUtc - startedAtUtc)
                            .TotalMilliseconds),
                Metadata = metadata
            };
        }
    }
}
