using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Validates stable HTTP and gRPC suppression failures.
    /// </summary>
    public sealed class RuntimePoolCapacitySuppressionCommandHandlerTests
    {
        /// <summary>
        /// Verifies the stable HTTP command handler returns the exact suppression reason.
        /// </summary>
        [Fact]
        public async Task Http_Handler_Should_Return_Capacity_Suppressed()
        {
            var routes =
                new InMemoryAiRuntimePoolRouteRegistry();

            await RegisterRouteAsync(
                routes,
                transportName: "http");

            var safety =
                await CreateSuppressedSafetyAsync();

            var transport =
                new RejectingHttpTransport();

            var handler =
                new AiRuntimePoolHttpCommandHandler(
                    new FakePoolManager(),
                    new AiRuntimePoolRouteForwarder(
                        routes,
                        safety),
                    transport);

            var result =
                await handler.HandleAsync(
                    CreateRequest());

            Assert.False(result.Success);

            Assert.Equal(
                AiRuntimePoolHttpRoutingFailureReasons
                    .CapacitySuppressed,
                result.FailureReason);

            Assert.Equal(0, transport.CallCount);
        }

        /// <summary>
        /// Verifies the stable gRPC command handler returns the exact suppression reason.
        /// </summary>
        [Fact]
        public async Task Grpc_Handler_Should_Return_Capacity_Suppressed()
        {
            var routes =
                new InMemoryAiRuntimePoolRouteRegistry();

            await RegisterRouteAsync(
                routes,
                transportName: "grpc");

            var safety =
                await CreateSuppressedSafetyAsync();

            var transport =
                new RejectingGrpcTransport();

            var handler =
                new AiRuntimePoolGrpcCommandHandler(
                    new FakePoolManager(),
                    new AiRuntimePoolRouteForwarder(
                        routes,
                        safety),
                    transport);

            var result =
                await handler.HandleAsync(
                    CreateRequest());

            Assert.False(result.Success);

            Assert.Equal(
                AiRuntimePoolGrpcRoutingFailureReasons
                    .CapacitySuppressed,
                result.FailureReason);

            Assert.Equal(0, transport.CallCount);
        }

        /// <summary>
        /// Registers one exact route for the selected transport.
        /// </summary>
        private static Task<AiRuntimePoolRouteDescriptor>
            RegisterRouteAsync(
                IAiRuntimePoolRouteRegistry routes,
                string transportName)
        {
            return routes.RegisterAsync(
                new AiRuntimePoolRouteRegistration
                {
                    RouteId = "route-a1",
                    PoolId = "pool-01",
                    HostId = "host-01",
                    RuntimeInstanceId =
                        "runtime-a1",
                    TransportName =
                        transportName,
                    TransportEndpoint =
                        "http://127.0.0.1:6101"
                });
        }

        /// <summary>
        /// Creates one registry containing exact A1 suppression.
        /// </summary>
        private static async Task<
            InMemoryAiRuntimePoolCapacitySafetyRegistry>
            CreateSuppressedSafetyAsync()
        {
            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await safety.SuppressAsync(
                RuntimePoolCapacitySafetyRegistryTests
                    .CreateSuppression(
                        failureId: "failure-a1",
                        runtimeInstanceId: "runtime-a1",
                        routeId: "route-a1"));

            return safety;
        }

        /// <summary>
        /// Creates one existing exact runtime command request.
        /// </summary>
        private static AiRuntimeInstanceCommandRequest
            CreateRequest()
        {
            return new AiRuntimeInstanceCommandRequest
            {
                Operation =
                    AiRuntimeInstanceCommandOperation
                        .GetQueueStatus,
                RuntimeInstanceId =
                    "runtime-a1"
            };
        }

        /// <summary>
        /// Provides deterministic local pool identity.
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
        /// Rejects any HTTP call because suppressed capacity must never reach transport.
        /// </summary>
        private sealed class RejectingHttpTransport :
            IAiRuntimePoolHttpTransportForwarder
        {
            /// <summary>
            /// Gets the transport invocation count.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> ForwardAsync(
                AiRuntimePoolRouteDescriptor route,
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;

                throw new InvalidOperationException(
                    "Suppressed HTTP capacity reached transport.");
            }
        }

        /// <summary>
        /// Rejects any gRPC call because suppressed capacity must never reach transport.
        /// </summary>
        private sealed class RejectingGrpcTransport :
            IAiRuntimePoolGrpcTransportForwarder
        {
            /// <summary>
            /// Gets the transport invocation count.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCommandResult> ForwardAsync(
                AiRuntimePoolRouteDescriptor route,
                AiRuntimeInstanceCommandRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;

                throw new InvalidOperationException(
                    "Suppressed gRPC capacity reached transport.");
            }
        }
    }
}
