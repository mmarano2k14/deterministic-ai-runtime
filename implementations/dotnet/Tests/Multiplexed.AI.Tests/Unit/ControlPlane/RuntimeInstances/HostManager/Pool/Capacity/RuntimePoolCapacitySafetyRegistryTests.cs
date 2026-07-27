using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Validates immutable exact runtime-instance capacity suppression.
    /// </summary>
    public sealed class RuntimePoolCapacitySafetyRegistryTests
    {
        /// <summary>
        /// Verifies that suppressing A1 does not contaminate A2.
        /// </summary>
        [Fact]
        public async Task SuppressAsync_Should_Store_Only_Exact_Runtime()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await registry.SuppressAsync(
                CreateSuppression(
                    failureId: "failure-a1",
                    runtimeInstanceId: "runtime-a1",
                    routeId: "route-a1"));

            var runtimeA1 =
                await registry.GetSuppressionAsync(
                    "pool-01",
                    "host-01",
                    "runtime-a1");

            var runtimeA2 =
                await registry.GetSuppressionAsync(
                    "pool-01",
                    "host-01",
                    "runtime-a2");

            var hostSuppressions =
                await registry.ListByHostIdAsync(
                    "host-01");

            Assert.NotNull(runtimeA1);
            Assert.Null(runtimeA2);

            var suppression =
                Assert.Single(hostSuppressions);

            Assert.Equal(
                "runtime-a1",
                suppression.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies idempotent concurrent suppression of one immutable runtime identity.
        /// </summary>
        [Fact]
        public async Task SuppressAsync_Should_Be_Idempotent_Under_Concurrency()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var suppression =
                CreateSuppression(
                    failureId: "failure-a1",
                    runtimeInstanceId: "runtime-a1",
                    routeId: "route-a1");

            var results =
                await Task.WhenAll(
                    Enumerable
                        .Range(0, 20)
                        .Select(
                            _ =>
                                registry.SuppressAsync(
                                    suppression)));

            Assert.All(
                results,
                result =>
                    Assert.Equal(
                        suppression,
                        result));

            Assert.Single(
                await registry.ListByHostIdAsync(
                    "host-01"));
        }

        /// <summary>
        /// Verifies that an immutable RuntimeInstanceId cannot be rebound to another failure.
        /// </summary>
        [Fact]
        public async Task SuppressAsync_Should_Reject_Runtime_Rebinding()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await registry.SuppressAsync(
                CreateSuppression(
                    failureId: "failure-a1",
                    runtimeInstanceId: "runtime-a1",
                    routeId: "route-a1"));

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolCapacitySuppressionConflictException>(
                    () =>
                        registry.SuppressAsync(
                            CreateSuppression(
                                failureId: "failure-other",
                                runtimeInstanceId:
                                    "runtime-a1",
                                routeId:
                                    "route-other")));

            Assert.Equal(
                "runtime-a1",
                exception.RuntimeInstanceId);
        }

        /// <summary>
        /// Creates one deterministic exact suppression.
        /// </summary>
        internal static AiRuntimePoolCapacitySuppression
            CreateSuppression(
                string failureId,
                string runtimeInstanceId,
                string routeId)
        {
            return new AiRuntimePoolCapacitySuppression
            {
                FailureId = failureId,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId =
                    runtimeInstanceId,
                RouteId = routeId,
                SuppressedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)
            };
        }
    }
}
