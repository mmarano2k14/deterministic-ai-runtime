using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Validates exact protocol-neutral forwarding and forwarding-lease release.
    /// </summary>
    public sealed class RuntimePoolRouteForwarderTests
    {
        /// <summary>
        /// Verifies that the callback receives only the exact resolved child route.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Invoke_Adapter_With_Exact_Route()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "http://127.0.0.1:6102"));

            var forwarder =
                new AiRuntimePoolRouteForwarder(
                    registry);

            AiRuntimePoolRouteDescriptor? observedRoute =
                null;

            var result =
                await forwarder.ForwardAsync(
                    CreateResolutionRequest(
                        "runtime-a2"),
                    (route, _) =>
                    {
                        observedRoute = route;
                        return Task.FromResult("response-a2");
                    });

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                result.Status);

            Assert.Equal(
                "response-a2",
                result.Response);

            Assert.Equal(
                "route-a2",
                result.RouteId);

            Assert.Equal(
                "runtime-a2",
                observedRoute?.RuntimeInstanceId);

            Assert.Equal(
                "http://127.0.0.1:6102",
                observedRoute?.TransportEndpoint);
        }

        /// <summary>
        /// Verifies that a missing target never invokes the transport callback.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Not_Invoke_Adapter_For_Missing_Runtime()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a1",
                    "runtime-a1",
                    "http://127.0.0.1:6101"));

            var callbackCount = 0;

            var result =
                await new AiRuntimePoolRouteForwarder(registry)
                    .ForwardAsync(
                        CreateResolutionRequest(
                            "runtime-a2"),
                        (_, _) =>
                        {
                            callbackCount++;
                            return Task.FromResult("unexpected");
                        });

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                result.Status);

            Assert.Equal(0, callbackCount);
            Assert.Null(result.Response);
        }

        /// <summary>
        /// Verifies that callback failure still releases the active route lease.
        /// </summary>
        [Fact]
        public async Task ForwardAsync_Should_Release_Lease_When_Adapter_Throws()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var route =
                await registry.RegisterAsync(
                    CreateRegistration(
                        "route-a2",
                        "runtime-a2",
                        "http://127.0.0.1:6102"));

            var forwarder =
                new AiRuntimePoolRouteForwarder(
                    registry);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    forwarder.ForwardAsync<string>(
                        CreateResolutionRequest(
                            "runtime-a2"),
                        (_, _) =>
                            throw new InvalidOperationException(
                                "synthetic-forward-failure")));

            await registry.BeginDrainAsync(
                CreateMutationRequest(route));

            var drained =
                await registry.WaitUntilDrainedAsync(
                    CreateMutationRequest(route));

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.Applied,
                drained.Status);
        }

        /// <summary>
        /// Creates one deterministic route registration.
        /// </summary>
        internal static AiRuntimePoolRouteRegistration
            CreateRegistration(
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
        /// Creates one exact forwarding request.
        /// </summary>
        internal static AiRuntimePoolRouteResolutionRequest
            CreateResolutionRequest(
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
        /// Creates one exact route mutation request.
        /// </summary>
        internal static AiRuntimePoolRouteMutationRequest
            CreateMutationRequest(
                AiRuntimePoolRouteDescriptor route)
        {
            return new AiRuntimePoolRouteMutationRequest
            {
                RouteId = route.RouteId,
                PoolId = route.PoolId,
                HostId = route.HostId,
                RuntimeInstanceId =
                    route.RuntimeInstanceId
            };
        }
    }
}
