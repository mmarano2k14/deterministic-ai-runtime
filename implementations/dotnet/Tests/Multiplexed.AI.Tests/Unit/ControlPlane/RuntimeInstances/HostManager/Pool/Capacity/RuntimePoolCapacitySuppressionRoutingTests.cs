using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Validates protocol-neutral exact capacity suppression during forwarding.
    /// </summary>
    public sealed class RuntimePoolCapacitySuppressionRoutingTests
    {
        /// <summary>
        /// Verifies that A1 is blocked while A2 remains independently routable.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Block_A1_And_Preserve_A2()
        {
            var routes =
                new InMemoryAiRuntimePoolRouteRegistry();

            await RegisterRouteAsync(
                routes,
                runtimeInstanceId: "runtime-a1",
                routeId: "route-a1");

            await RegisterRouteAsync(
                routes,
                runtimeInstanceId: "runtime-a2",
                routeId: "route-a2");

            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await safety.SuppressAsync(
                RuntimePoolCapacitySafetyRegistryTests
                    .CreateSuppression(
                        failureId: "failure-a1",
                        runtimeInstanceId: "runtime-a1",
                        routeId: "route-a1"));

            var forwarder =
                new AiRuntimePoolRouteForwarder(
                    routes,
                    safety);

            var invokedRuntimeIds =
                new List<string>();

            var runtimeA1 =
                await forwarder.ForwardAsync(
                    CreateRequest(
                        "runtime-a1"),
                    (route, _) =>
                    {
                        invokedRuntimeIds.Add(
                            route.RuntimeInstanceId);

                        return Task.FromResult(
                            route.RuntimeInstanceId);
                    });

            var runtimeA2 =
                await forwarder.ForwardAsync(
                    CreateRequest(
                        "runtime-a2"),
                    (route, _) =>
                    {
                        invokedRuntimeIds.Add(
                            route.RuntimeInstanceId);

                        return Task.FromResult(
                            route.RuntimeInstanceId);
                    });

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Suppressed,
                runtimeA1.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                runtimeA2.Status);

            Assert.Equal(
                "runtime-a2",
                runtimeA2.Response);

            Assert.Equal(
                new[] { "runtime-a2" },
                invokedRuntimeIds);
        }

        /// <summary>
        /// Verifies the second safety check after lease acquisition closes the lookup race.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Recheck_Suppression_After_Lease_Acquisition()
        {
            var routes =
                new InMemoryAiRuntimePoolRouteRegistry();

            await RegisterRouteAsync(
                routes,
                runtimeInstanceId: "runtime-a1",
                routeId: "route-a1");

            var safety =
                new TwoPhaseSafetyReader(
                    RuntimePoolCapacitySafetyRegistryTests
                        .CreateSuppression(
                            failureId: "failure-a1",
                            runtimeInstanceId: "runtime-a1",
                            routeId: "route-a1"));

            var forwarder =
                new AiRuntimePoolRouteForwarder(
                    routes,
                    safety);

            var transportInvocations = 0;

            var result =
                await forwarder.ForwardAsync(
                    CreateRequest(
                        "runtime-a1"),
                    (_, _) =>
                    {
                        transportInvocations++;
                        return Task.FromResult(true);
                    });

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Suppressed,
                result.Status);

            Assert.Equal(
                "route-a1",
                result.RouteId);

            Assert.Equal(
                0,
                transportInvocations);

            Assert.Equal(
                2,
                safety.ReadCount);
        }

        /// <summary>
        /// Registers one exact HTTP route.
        /// </summary>
        private static Task<AiRuntimePoolRouteDescriptor>
            RegisterRouteAsync(
                IAiRuntimePoolRouteRegistry registry,
                string runtimeInstanceId,
                string routeId)
        {
            return registry.RegisterAsync(
                new AiRuntimePoolRouteRegistration
                {
                    RouteId = routeId,
                    PoolId = "pool-01",
                    HostId = "host-01",
                    RuntimeInstanceId =
                        runtimeInstanceId,
                    TransportName = "http",
                    TransportEndpoint =
                        "http://127.0.0.1:6101"
                });
        }

        /// <summary>
        /// Creates one exact route request.
        /// </summary>
        private static AiRuntimePoolRouteResolutionRequest
            CreateRequest(
                string runtimeInstanceId)
        {
            return new AiRuntimePoolRouteResolutionRequest
            {
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                TransportName = "http"
            };
        }

        /// <summary>
        /// Returns safe on the first read and suppressed on the second read.
        /// </summary>
        private sealed class TwoPhaseSafetyReader :
            IAiRuntimePoolCapacitySafetyReader
        {
            private readonly AiRuntimePoolCapacitySuppression
                suppression;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="TwoPhaseSafetyReader"/> class.
            /// </summary>
            public TwoPhaseSafetyReader(
                AiRuntimePoolCapacitySuppression suppression)
            {
                this.suppression = suppression;
            }

            /// <summary>
            /// Gets the exact safety-read count.
            /// </summary>
            public int ReadCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimePoolCapacitySuppression?>
                GetSuppressionAsync(
                    string poolId,
                    string hostId,
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                this.ReadCount++;

                return Task.FromResult<
                    AiRuntimePoolCapacitySuppression?>(
                    this.ReadCount == 1
                        ? null
                        : this.suppression);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<
                AiRuntimePoolCapacitySuppression>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                IReadOnlyList<
                    AiRuntimePoolCapacitySuppression> suppressions =
                    new[] { this.suppression };

                return Task.FromResult(suppressions);
            }
        }
    }
}
