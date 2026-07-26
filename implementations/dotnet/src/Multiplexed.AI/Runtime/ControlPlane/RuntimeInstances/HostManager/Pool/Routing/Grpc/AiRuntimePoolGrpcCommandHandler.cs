using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Routes stable gRPC pool commands to exact process-host runtime instances.
    /// </summary>
    public sealed class AiRuntimePoolGrpcCommandHandler :
        IAiRuntimePoolGrpcCommandHandler
    {
        private readonly IAiRuntimeProcessPoolManager poolManager;
        private readonly IAiRuntimePoolRouteForwarder routeForwarder;
        private readonly IAiRuntimePoolGrpcTransportForwarder
            transportForwarder;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolGrpcCommandHandler"/> class.
        /// </summary>
        public AiRuntimePoolGrpcCommandHandler(
            IAiRuntimeProcessPoolManager poolManager,
            IAiRuntimePoolRouteForwarder routeForwarder,
            IAiRuntimePoolGrpcTransportForwarder transportForwarder)
        {
            this.poolManager =
                poolManager
                ?? throw new ArgumentNullException(nameof(poolManager));

            this.routeForwarder =
                routeForwarder
                ?? throw new ArgumentNullException(nameof(routeForwarder));

            this.transportForwarder =
                transportForwarder
                ?? throw new ArgumentNullException(
                    nameof(transportForwarder));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceCommandResult> HandleAsync(
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            var targetRuntimeInstanceId =
                ResolveTargetRuntimeInstanceId(request);

            if (string.IsNullOrWhiteSpace(
                    targetRuntimeInstanceId))
            {
                return CreateFailure(
                    request,
                    string.Empty,
                    startedAtUtc,
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .RuntimeInstanceIdMissing,
                    "The Runtime Pool gRPC command requires an exact RuntimeInstanceId.",
                    routeStatus: null,
                    exception: null);
            }

            try
            {
                var forwarding =
                    await this.routeForwarder
                        .ForwardAsync(
                            new AiRuntimePoolRouteResolutionRequest
                            {
                                PoolId =
                                    this.poolManager.Identity.PoolId,
                                HostId =
                                    this.poolManager.Identity.HostId,
                                RuntimeInstanceId =
                                    targetRuntimeInstanceId,
                                TransportName = "grpc"
                            },
                            (route, token) =>
                                this.transportForwarder
                                    .ForwardAsync(
                                        route,
                                        request,
                                        token),
                            cancellationToken)
                        .ConfigureAwait(false);

                if (forwarding.Status ==
                        AiRuntimePoolRouteResolutionStatus.Resolved &&
                    forwarding.Response is not null)
                {
                    return forwarding.Response;
                }

                return CreateRoutingFailure(
                    request,
                    targetRuntimeInstanceId,
                    startedAtUtc,
                    forwarding.Status);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return CreateFailure(
                    request,
                    targetRuntimeInstanceId,
                    startedAtUtc,
                    AiRuntimePoolGrpcRoutingFailureReasons
                        .ForwardingFailed,
                    exception.Message,
                    routeStatus: null,
                    exception);
            }
        }

        /// <summary>
        /// Resolves the exact target identity from the existing command request.
        /// </summary>
        private static string ResolveTargetRuntimeInstanceId(
            AiRuntimeInstanceCommandRequest request)
        {
            return (
                    request.DispatchRequest?.RuntimeInstanceId ??
                    request.RuntimeInstanceId ??
                    string.Empty)
                .Trim();
        }

        /// <summary>
        /// Creates an explicit routing failure result.
        /// </summary>
        private AiRuntimeInstanceCommandResult CreateRoutingFailure(
            AiRuntimeInstanceCommandRequest request,
            string targetRuntimeInstanceId,
            DateTimeOffset startedAtUtc,
            AiRuntimePoolRouteResolutionStatus status)
        {
            var failureReason =
                status switch
                {
                    AiRuntimePoolRouteResolutionStatus.NotFound =>
                        AiRuntimePoolGrpcRoutingFailureReasons.RouteNotFound,
                    AiRuntimePoolRouteResolutionStatus.PoolMismatch =>
                        AiRuntimePoolGrpcRoutingFailureReasons.PoolMismatch,
                    AiRuntimePoolRouteResolutionStatus.HostMismatch =>
                        AiRuntimePoolGrpcRoutingFailureReasons.HostMismatch,
                    AiRuntimePoolRouteResolutionStatus.TransportMismatch =>
                        AiRuntimePoolGrpcRoutingFailureReasons
                            .TransportMismatch,
                    AiRuntimePoolRouteResolutionStatus.Draining =>
                        AiRuntimePoolGrpcRoutingFailureReasons.RouteDraining,
                    _ =>
                        AiRuntimePoolGrpcRoutingFailureReasons
                            .ForwardingFailed
                };

            return CreateFailure(
                request,
                targetRuntimeInstanceId,
                startedAtUtc,
                failureReason,
                $"Runtime Pool route resolution failed with status '{status}'.",
                status,
                exception: null);
        }

        /// <summary>
        /// Creates one failed existing command result with routing diagnostics.
        /// </summary>
        private AiRuntimeInstanceCommandResult CreateFailure(
            AiRuntimeInstanceCommandRequest request,
            string targetRuntimeInstanceId,
            DateTimeOffset startedAtUtc,
            string failureReason,
            string message,
            AiRuntimePoolRouteResolutionStatus? routeStatus,
            Exception? exception)
        {
            var completedAtUtc =
                DateTimeOffset.UtcNow;

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["runtime.pool.routing.failure"] = "true",
                    ["runtime.pool.id"] =
                        this.poolManager.Identity.PoolId,
                    ["runtime.pool.host.id"] =
                        this.poolManager.Identity.HostId,
                    ["target.runtime.instance.id"] =
                        targetRuntimeInstanceId,
                    ["transport.name"] = "grpc"
                };

            if (routeStatus.HasValue)
            {
                metadata["runtime.pool.route.status"] =
                    routeStatus.Value.ToString();
            }

            if (exception is not null)
            {
                metadata["exception.type"] =
                    exception.GetType().FullName ??
                    exception.GetType().Name;
            }

            return new AiRuntimeInstanceCommandResult
            {
                Success = false,
                Operation = request.Operation,
                RuntimeInstanceId =
                    targetRuntimeInstanceId,
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
