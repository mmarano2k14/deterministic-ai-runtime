using System;
using System.Linq;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Validates host-membership suppression without inventing Pod-local route identities.
    /// </summary>
    public sealed class RuntimePoolHostMembershipCapacitySafetyTests
    {
        /// <summary>
        /// Verifies one exact Pod membership is stored atomically with no local RouteId.
        /// </summary>
        [Fact]
        public async Task SuppressBatchAsync_Should_Store_Exact_Host_Membership()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var suppressedAtUtc = DateTimeOffset.UtcNow;

            var stored =
                await registry.SuppressBatchAsync(
                    new[]
                    {
                        CreateHostSuppression("runtime-a-3", suppressedAtUtc),
                        CreateHostSuppression("runtime-a-1", suppressedAtUtc),
                        CreateHostSuppression("runtime-a-2", suppressedAtUtc)
                    });

            Assert.Equal(
                new[]
                {
                    "runtime-a-1",
                    "runtime-a-2",
                    "runtime-a-3"
                },
                stored
                    .Select(item => item.RuntimeInstanceId)
                    .ToArray());

            Assert.All(
                stored,
                suppression =>
                {
                    Assert.Equal(
                        AiRuntimePoolCapacitySuppressionScope.HostMembership,
                        suppression.Scope);
                    Assert.Null(suppression.RouteId);
                    Assert.Equal("pool-a", suppression.PoolId);
                    Assert.Equal("pod-a", suppression.HostId);
                });

            Assert.Null(
                await registry.GetSuppressionAsync(
                    "pool-a",
                    "pod-b",
                    "runtime-b-1"));
        }

        /// <summary>
        /// Verifies route-scoped suppression cannot omit the exact RouteId.
        /// </summary>
        [Fact]
        public async Task SuppressAsync_Should_Reject_Route_Scope_Without_RouteId()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await Assert.ThrowsAsync<ArgumentException>(
                () => registry.SuppressAsync(
                    new AiRuntimePoolCapacitySuppression
                    {
                        FailureId = "failure-a",
                        PoolId = "pool-a",
                        HostId = "pod-a",
                        Scope =
                            AiRuntimePoolCapacitySuppressionScope
                                .RuntimeInstanceRoute,
                        RuntimeInstanceId = "runtime-a-1",
                        RouteId = null,
                        SuppressedAtUtc = DateTimeOffset.UtcNow
                    }));
        }

        /// <summary>
        /// Verifies host-membership suppression cannot carry one Pod-local RouteId.
        /// </summary>
        [Fact]
        public async Task SuppressAsync_Should_Reject_Host_Scope_With_RouteId()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await Assert.ThrowsAsync<ArgumentException>(
                () => registry.SuppressAsync(
                    new AiRuntimePoolCapacitySuppression
                    {
                        FailureId = "failure-a",
                        PoolId = "pool-a",
                        HostId = "pod-a",
                        Scope =
                            AiRuntimePoolCapacitySuppressionScope
                                .HostMembership,
                        RuntimeInstanceId = "runtime-a-1",
                        RouteId = "route-local-a-1",
                        SuppressedAtUtc = DateTimeOffset.UtcNow
                    }));
        }

        private static AiRuntimePoolCapacitySuppression
            CreateHostSuppression(
                string runtimeInstanceId,
                DateTimeOffset suppressedAtUtc)
        {
            return new AiRuntimePoolCapacitySuppression
            {
                FailureId = "failure-a",
                PoolId = "pool-a",
                HostId = "pod-a",
                Scope =
                    AiRuntimePoolCapacitySuppressionScope
                        .HostMembership,
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = null,
                SuppressedAtUtc = suppressedAtUtc
            };
        }
    }
}
