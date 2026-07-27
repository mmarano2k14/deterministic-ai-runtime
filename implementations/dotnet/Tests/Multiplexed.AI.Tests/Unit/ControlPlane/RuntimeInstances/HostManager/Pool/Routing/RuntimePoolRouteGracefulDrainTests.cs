using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Validates graceful route draining while exact forwarding operations remain active.
    /// </summary>
    public sealed class RuntimePoolRouteGracefulDrainTests
    {
        /// <summary>
        /// Verifies that drain rejects new requests and waits for the current forwarding lease.
        /// </summary>
        [Fact]
        public async Task Drain_Should_Wait_For_Active_Forward_And_Reject_New_Lease()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var route =
                await registry.RegisterAsync(
                    RuntimePoolRouteForwarderTests.CreateRegistration(
                        "route-a2",
                        "runtime-a2",
                        "http://127.0.0.1:6102"));

            var acquisition =
                await registry.AcquireForwardingLeaseAsync(
                    RuntimePoolRouteForwarderTests
                        .CreateResolutionRequest(
                            "runtime-a2"));

            var lease =
                Assert.IsAssignableFrom<
                    IAiRuntimePoolRouteLease>(
                    acquisition.Lease);

            await registry.BeginDrainAsync(
                RuntimePoolRouteForwarderTests
                    .CreateMutationRequest(route));

            var rejected =
                await registry.AcquireForwardingLeaseAsync(
                    RuntimePoolRouteForwarderTests
                        .CreateResolutionRequest(
                            "runtime-a2"));

            var waitTask =
                registry.WaitUntilDrainedAsync(
                    RuntimePoolRouteForwarderTests
                        .CreateMutationRequest(route));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Draining,
                rejected.Status);

            Assert.False(waitTask.IsCompleted);

            await lease.DisposeAsync();

            var drained =
                await waitTask.WaitAsync(
                    TimeSpan.FromSeconds(1));

            Assert.Equal(
                AiRuntimePoolRouteMutationStatus.Applied,
                drained.Status);
        }

        /// <summary>
        /// Verifies that draining A1 never blocks or redirects A2.
        /// </summary>
        [Fact]
        public async Task Drain_A1_Should_Not_Block_Forwarding_To_A2()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var routeA1 =
                await registry.RegisterAsync(
                    RuntimePoolRouteForwarderTests.CreateRegistration(
                        "route-a1",
                        "runtime-a1",
                        "http://127.0.0.1:6101"));

            await registry.RegisterAsync(
                RuntimePoolRouteForwarderTests.CreateRegistration(
                    "route-a2",
                    "runtime-a2",
                    "http://127.0.0.1:6102"));

            await registry.BeginDrainAsync(
                RuntimePoolRouteForwarderTests
                    .CreateMutationRequest(routeA1));

            var result =
                await new AiRuntimePoolRouteForwarder(registry)
                    .ForwardAsync(
                        RuntimePoolRouteForwarderTests
                            .CreateResolutionRequest(
                                "runtime-a2"),
                        (route, _) =>
                            Task.FromResult(
                                route.RuntimeInstanceId));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                result.Status);

            Assert.Equal(
                "runtime-a2",
                result.Response);
        }

        /// <summary>
        /// Verifies that releasing a removed route lease cannot alter a replacement route.
        /// </summary>
        [Fact]
        public async Task Stale_Lease_Release_Should_Not_Mutate_Replacement_Route()
        {
            var registry =
                new InMemoryAiRuntimePoolRouteRegistry();

            var oldRoute =
                await registry.RegisterAsync(
                    RuntimePoolRouteForwarderTests.CreateRegistration(
                        "route-old",
                        "runtime-a2",
                        "http://127.0.0.1:6102"));

            var acquisition =
                await registry.AcquireForwardingLeaseAsync(
                    RuntimePoolRouteForwarderTests
                        .CreateResolutionRequest(
                            "runtime-a2"));

            var lease =
                Assert.IsAssignableFrom<
                    IAiRuntimePoolRouteLease>(
                    acquisition.Lease);

            await registry.RemoveAsync(
                RuntimePoolRouteForwarderTests
                    .CreateMutationRequest(oldRoute));

            var replacement =
                await registry.RegisterAsync(
                    RuntimePoolRouteForwarderTests.CreateRegistration(
                        "route-new",
                        "runtime-a2",
                        "http://127.0.0.1:6199"));

            await lease.DisposeAsync();

            var resolved =
                await registry.ResolveAsync(
                    RuntimePoolRouteForwarderTests
                        .CreateResolutionRequest(
                            "runtime-a2"));

            Assert.Equal(
                AiRuntimePoolRouteResolutionStatus.Resolved,
                resolved.Status);

            Assert.Equal(
                replacement.RouteId,
                resolved.Route?.RouteId);

            Assert.Equal(
                "http://127.0.0.1:6199",
                resolved.Route?.TransportEndpoint);
        }
    }
}
