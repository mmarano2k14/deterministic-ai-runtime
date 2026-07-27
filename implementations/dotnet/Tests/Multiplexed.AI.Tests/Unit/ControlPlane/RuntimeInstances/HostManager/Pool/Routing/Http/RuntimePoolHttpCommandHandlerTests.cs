using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Validates exact stable HTTP command routing.
    /// </summary>
    public sealed class RuntimePoolHttpCommandHandlerTests
    {
        /// <summary>
        /// Verifies forwarding to the exact requested runtime only.
        /// </summary>
        [Fact]
        public async Task HandleAsync_Should_Forward_Only_To_Exact_Runtime()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRoute(
                    "route-a1",
                    "runtime-a1",
                    "http://127.0.0.1:6101"));

            await registry.RegisterAsync(
                CreateRoute(
                    "route-a2",
                    "runtime-a2",
                    "http://127.0.0.1:6102"));

            var transport =
                new RecordingTransportForwarder();

            var handler =
                CreateHandler(
                    registry,
                    transport);

            var result =
                await handler.HandleAsync(
                    CreateRequest(
                        "runtime-a2"));

            Assert.True(result.Success);
            Assert.Equal(
                "runtime-a2",
                result.RuntimeInstanceId);

            Assert.Equal(1, transport.CallCount);
            Assert.Equal(
                "runtime-a2",
                transport.ObservedRoute?
                    .RuntimeInstanceId);

            Assert.Equal(
                "http://127.0.0.1:6102",
                transport.ObservedRoute?
                    .TransportEndpoint);
        }

        /// <summary>
        /// Verifies that a missing runtime never invokes the HTTP adapter.
        /// </summary>
        [Fact]
        public async Task HandleAsync_Should_Return_NotFound_Without_Sibling_Fallback()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRoute(
                    "route-a1",
                    "runtime-a1",
                    "http://127.0.0.1:6101"));

            var transport =
                new RecordingTransportForwarder();

            var result =
                await CreateHandler(
                        registry,
                        transport)
                    .HandleAsync(
                        CreateRequest(
                            "runtime-a2"));

            Assert.False(result.Success);

            Assert.Equal(
                AiRuntimePoolHttpRoutingFailureReasons
                    .RouteNotFound,
                result.FailureReason);

            Assert.Equal(0, transport.CallCount);
        }

        /// <summary>
        /// Verifies that a draining exact route rejects the command before transport.
        /// </summary>
        [Fact]
        public async Task HandleAsync_Should_Reject_Draining_Runtime()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var route =
                await registry.RegisterAsync(
                    CreateRoute(
                        "route-a2",
                        "runtime-a2",
                        "http://127.0.0.1:6102"));

            await registry.BeginDrainAsync(
                new AiRuntimePoolRouteMutationRequest
                {
                    RouteId = route.RouteId,
                    PoolId = route.PoolId,
                    HostId = route.HostId,
                    RuntimeInstanceId =
                        route.RuntimeInstanceId
                });

            var transport =
                new RecordingTransportForwarder();

            var result =
                await CreateHandler(
                        registry,
                        transport)
                    .HandleAsync(
                        CreateRequest(
                            "runtime-a2"));

            Assert.False(result.Success);

            Assert.Equal(
                AiRuntimePoolHttpRoutingFailureReasons
                    .RouteDraining,
                result.FailureReason);

            Assert.Equal(0, transport.CallCount);
        }

        /// <summary>
        /// Creates one stable HTTP command handler.
        /// </summary>
        private static AiRuntimePoolHttpCommandHandler
            CreateHandler(
                IAiRuntimePoolRouteRegistry registry,
                IAiRuntimePoolHttpTransportForwarder transport)
        {
            return new AiRuntimePoolHttpCommandHandler(
                new FakePoolManager(),
                new AiRuntimePoolRouteForwarder(
                    registry),
                transport);
        }

        /// <summary>
        /// Creates one exact ready route.
        /// </summary>
        private static AiRuntimePoolRouteRegistration
            CreateRoute(
                string routeId,
                string runtimeInstanceId,
                string endpoint)
        {
            return new AiRuntimePoolRouteRegistration
            {
                RouteId = routeId,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                TransportName = "http",
                TransportEndpoint = endpoint
            };
        }

        /// <summary>
        /// Creates one existing runtime command request.
        /// </summary>
        internal static AiRuntimeInstanceCommandRequest
            CreateRequest(
                string runtimeInstanceId)
        {
            return new AiRuntimeInstanceCommandRequest
            {
                Operation =
                    AiRuntimeInstanceCommandOperation
                        .GetQueueStatus,
                RuntimeInstanceId =
                    runtimeInstanceId
            };
        }

        /// <summary>
        /// Provides deterministic process-pool identity for HTTP routing tests.
        /// </summary>
        private sealed class FakePoolManager :
            IAiRuntimeProcessPoolManager
        {
            /// <inheritdoc />
            public AiRuntimeProcessPoolIdentity Identity { get; } =
                new()
                {
                    PoolId = "pool-01",
                    HostId = "host-01",
                    RuntimeInstanceIdPrefix = "runtime"
                };

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot>
                EnsureInitialCapacityAsync(
                    CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot>
                EnsureCapacityAsync(
                    int requiredProcessCount,
                    CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolSnapshot>
                GetSnapshotAsync(
                    CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>
        /// Records exact routes received from the stable HTTP command handler.
        /// </summary>
        private sealed class RecordingTransportForwarder :
            IAiRuntimePoolHttpTransportForwarder
        {
            /// <summary>
            /// Gets the invocation count.
            /// </summary>
            public int CallCount { get; private set; }

            /// <summary>
            /// Gets the exact observed route.
            /// </summary>
            public AiRuntimePoolRouteDescriptor? ObservedRoute
            {
                get;
                private set;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> ForwardAsync(
                AiRuntimePoolRouteDescriptor route,
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.ObservedRoute = route;

                return Task.FromResult(
                    new AiRuntimeInstanceCommandResult
                    {
                        Success = true,
                        Operation = request.Operation,
                        RuntimeInstanceId =
                            route.RuntimeInstanceId,
                        StartedAtUtc =
                            DateTimeOffset.UtcNow,
                        CompletedAtUtc =
                            DateTimeOffset.UtcNow,
                        DurationMs = 0
                    });
            }
        }
    }
}
