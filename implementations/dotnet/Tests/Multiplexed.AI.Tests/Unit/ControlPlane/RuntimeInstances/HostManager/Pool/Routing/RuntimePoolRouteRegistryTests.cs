using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Validates exact protocol-neutral routing for independently registered runtime instances.
    /// </summary>
    public sealed class RuntimePoolRouteRegistryTests
    {
        /// <summary>
        /// Verifies exact route resolution for every authoritative identity.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Return_Only_Exact_Ready_Route()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var registered =
                await registry.RegisterAsync(
                    CreateRegistration(
                        "route-a2",
                        "runtime-a2",
                        "http",
                        "http://127.0.0.1:6102"));

            var result =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a2",
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                result.Status);

            Assert.Equal(
                registered,
                result.Route);
        }

        /// <summary>
        /// Verifies that a missing target never falls back to a healthy sibling.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_Should_Not_Fallback_To_Sibling_Runtime()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a1",
                    "runtime-a1",
                    "http",
                    "http://127.0.0.1:6101"));

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a3",
                    "runtime-a3",
                    "http",
                    "http://127.0.0.1:6103"));

            var result =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a2",
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.NotFound,
                result.Status);

            Assert.Null(result.Route);
        }

        /// <summary>
        /// Verifies explicit mismatch states without exposing another route endpoint.
        /// </summary>
        [Theory]
        [InlineData(
            "wrong-pool",
            "host-01",
            "http",
            AiRuntimePoolRouteResolutionStatus.PoolMismatch)]
        [InlineData(
            "pool-01",
            "wrong-host",
            "http",
            AiRuntimePoolRouteResolutionStatus.HostMismatch)]
        [InlineData(
            "pool-01",
            "host-01",
            "grpc",
            AiRuntimePoolRouteResolutionStatus.TransportMismatch)]
        public async Task ResolveAsync_Should_Reject_Authority_Mismatch(
            string poolId,
            string hostId,
            string transportName,
            AiRuntimePoolRouteResolutionStatus expectedStatus)
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "http",
                    "http://127.0.0.1:6102"));

            var result =
                await registry.ResolveAsync(
                    new AiRuntimePoolRouteResolutionRequest
                    {
                        PoolId = poolId,
                        HostId = hostId,
                        RuntimeInstanceId = "runtime-a2",
                        TransportName = transportName
                    });

            Assert.Equal(
                expectedStatus,
                result.Status);

            Assert.Null(result.Route);
        }

        /// <summary>
        /// Verifies that draining rejects new resolution while preserving route identity.
        /// </summary>
        [Fact]
        public async Task BeginDrainAsync_Should_Reject_New_Route_Resolution()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var route =
                await registry.RegisterAsync(
                    CreateRegistration(
                        "route-a2",
                        "runtime-a2",
                        "http",
                        "http://127.0.0.1:6102"));

            var drain =
                await registry.BeginDrainAsync(
                    CreateMutationRequest(route));

            var resolution =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        "runtime-a2",
                        "http"));

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.Applied,
                drain.Status);

            Assert.Equal(
                AiRuntimePoolRouteStatus.Draining,
                drain.Route?.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Draining,
                resolution.Status);

            Assert.Null(resolution.Route);
        }

        /// <summary>
        /// Verifies that stale route authority cannot drain or remove the current route.
        /// </summary>
        [Fact]
        public async Task Mutation_Should_Reject_Stale_RouteId()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var route =
                await registry.RegisterAsync(
                    CreateRegistration(
                        "route-current",
                        "runtime-a2",
                        "http",
                        "http://127.0.0.1:6102"));

            var staleRequest =
                new AiRuntimePoolRouteMutationRequest
                {
                    RouteId = "route-stale",
                    PoolId = route.PoolId,
                    HostId = route.HostId,
                    RuntimeInstanceId =
                        route.RuntimeInstanceId
                };

            var drain =
                await registry.BeginDrainAsync(
                    staleRequest);

            var remove =
                await registry.RemoveAsync(
                    staleRequest);

            var resolved =
                await registry.ResolveAsync(
                    CreateResolutionRequest(
                        route.RuntimeInstanceId,
                        route.TransportName));

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.IdentityMismatch,
                drain.Status);

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.IdentityMismatch,
                remove.Status);

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                resolved.Status);
        }

        /// <summary>
        /// Verifies idempotent concurrent registration of one exact route incarnation.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Be_Idempotent_Under_Concurrency()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var registration =
                CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "grpc",
                    "http://127.0.0.1:6202");

            var registrations =
                await Task.WhenAll(
                    Enumerable
                        .Range(0, 20)
                        .Select(
                            _ =>
                                registry.RegisterAsync(
                                    registration)));

            Assert.All(
                registrations,
                route =>
                    Assert.Equal(
                        "route-a2",
                        route.RouteId));

            var routes =
                await registry.ListByHostIdAsync(
                    "host-01");

            Assert.Single(routes);
        }

        /// <summary>
        /// Verifies that conflicting rebinding of one runtime identity is rejected.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Reject_Conflicting_Rebind()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "http",
                    "http://127.0.0.1:6102"));

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolRouteConflictException>(
                    () =>
                        registry.RegisterAsync(
                            CreateRegistration(
                                "route-a2-new",
                                "runtime-a2",
                                "http",
                                "http://127.0.0.1:6199")));

            Assert.Equal(
                "runtime-a2",
                exception.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that exact removal leaves every sibling route untouched.
        /// </summary>
        [Fact]
        public async Task RemoveAsync_Should_Remove_Only_Exact_Runtime_Route()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var routeA1 =
                await registry.RegisterAsync(
                    CreateRegistration(
                        "route-a1",
                        "runtime-a1",
                        "http",
                        "http://127.0.0.1:6101"));

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "http",
                    "http://127.0.0.1:6102"));

            await registry.RegisterAsync(
                CreateRegistration(
                    "route-a3",
                    "runtime-a3",
                    "http",
                    "http://127.0.0.1:6103"));

            var removed =
                await registry.RemoveAsync(
                    CreateMutationRequest(routeA1));

            var routes =
                await registry.ListByHostIdAsync(
                    "host-01");

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.Applied,
                removed.Status);

            Assert.Equal(
                new[]
                {
                    "runtime-a2",
                    "runtime-a3"
                },
                routes
                    .Select(
                        route =>
                            route.RuntimeInstanceId)
                    .ToArray());
        }

        /// <summary>
        /// Creates one deterministic route registration.
        /// </summary>
        private static AiRuntimePoolRouteRegistration
            CreateRegistration(
                string routeId,
                string runtimeInstanceId,
                string transportName,
                string transportEndpoint)
        {
            return new AiRuntimePoolRouteRegistration
            {
                RouteId = routeId,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = runtimeInstanceId,
                TransportName = transportName,
                TransportEndpoint = transportEndpoint
            };
        }

        /// <summary>
        /// Creates one exact route-resolution request.
        /// </summary>
        private static AiRuntimePoolRouteResolutionRequest
            CreateResolutionRequest(
                string runtimeInstanceId,
                string transportName)
        {
            return new AiRuntimePoolRouteResolutionRequest
            {
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = runtimeInstanceId,
                TransportName = transportName
            };
        }

        /// <summary>
        /// Creates one exact route mutation request.
        /// </summary>
        private static AiRuntimePoolRouteMutationRequest
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
