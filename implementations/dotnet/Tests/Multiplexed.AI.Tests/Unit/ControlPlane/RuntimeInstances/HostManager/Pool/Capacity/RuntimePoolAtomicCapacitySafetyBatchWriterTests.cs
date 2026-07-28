using System;
using System.Linq;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Validates the atomic visibility boundary for multi-runtime capacity suppression.
    /// </summary>
    public sealed class RuntimePoolAtomicCapacitySafetyBatchWriterTests
    {
        /// <summary>
        /// Verifies that one batch publishes all exact suppressions together.
        /// </summary>
        [Fact]
        public async Task SuppressBatchAsync_Should_Store_Complete_Exact_Set()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var suppressedAtUtc = DateTimeOffset.UtcNow;

            var stored =
                await registry.SuppressBatchAsync(
                    new[]
                    {
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-3",
                            "route-a-3",
                            suppressedAtUtc),
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-1",
                            "route-a-1",
                            suppressedAtUtc),
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-2",
                            "route-a-2",
                            suppressedAtUtc)
                    });

            Assert.Equal(3, stored.Count);
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

            var authoritative =
                await registry.ListByHostIdAsync("pod-a");

            Assert.Equal(3, authoritative.Count);
            Assert.All(
                authoritative,
                suppression =>
                {
                    Assert.Equal("failure-pod-a", suppression.FailureId);
                    Assert.Equal("pool-a", suppression.PoolId);
                    Assert.Equal("pod-a", suppression.HostId);
                    Assert.Equal(
                        suppressedAtUtc,
                        suppression.SuppressedAtUtc);
                });
        }

        /// <summary>
        /// Verifies that one conflicting member prevents every new batch write.
        /// </summary>
        [Fact]
        public async Task SuppressBatchAsync_Should_Not_Partially_Apply_On_Conflict()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var existingAtUtc = DateTimeOffset.UtcNow;

            await registry.SuppressAsync(
                CreateSuppression(
                    "failure-existing",
                    "pool-a",
                    "pod-a",
                    "runtime-a-2",
                    "route-a-2",
                    existingAtUtc));

            var batchAtUtc = existingAtUtc.AddSeconds(1);

            await Assert.ThrowsAsync<
                AiRuntimePoolCapacitySuppressionConflictException>(
                () => registry.SuppressBatchAsync(
                    new[]
                    {
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-1",
                            "route-a-1",
                            batchAtUtc),
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-2",
                            "route-a-2",
                            batchAtUtc),
                        CreateSuppression(
                            "failure-pod-a",
                            "pool-a",
                            "pod-a",
                            "runtime-a-3",
                            "route-a-3",
                            batchAtUtc)
                    }));

            Assert.Null(
                await registry.GetSuppressionAsync(
                    "pool-a",
                    "pod-a",
                    "runtime-a-1"));

            Assert.Null(
                await registry.GetSuppressionAsync(
                    "pool-a",
                    "pod-a",
                    "runtime-a-3"));

            var existing =
                await registry.GetSuppressionAsync(
                    "pool-a",
                    "pod-a",
                    "runtime-a-2");

            Assert.NotNull(existing);
            Assert.Equal("failure-existing", existing!.FailureId);
        }

        /// <summary>
        /// Verifies exact idempotency for an already stored batch.
        /// </summary>
        [Fact]
        public async Task SuppressBatchAsync_Should_Be_Idempotent_For_Same_Batch()
        {
            var registry =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var suppressedAtUtc = DateTimeOffset.UtcNow;

            var batch =
                new[]
                {
                    CreateSuppression(
                        "failure-pod-a",
                        "pool-a",
                        "pod-a",
                        "runtime-a-1",
                        "route-a-1",
                        suppressedAtUtc),
                    CreateSuppression(
                        "failure-pod-a",
                        "pool-a",
                        "pod-a",
                        "runtime-a-2",
                        "route-a-2",
                        suppressedAtUtc)
                };

            var first = await registry.SuppressBatchAsync(batch);
            var second = await registry.SuppressBatchAsync(batch);

            Assert.Equal(first.ToArray(), second.ToArray());
            Assert.Equal(
                2,
                (await registry.ListByHostIdAsync("pod-a")).Count);
        }

        /// <summary>
        /// Creates one exact immutable capacity suppression.
        /// </summary>
        private static AiRuntimePoolCapacitySuppression CreateSuppression(
            string failureId,
            string poolId,
            string hostId,
            string runtimeInstanceId,
            string routeId,
            DateTimeOffset suppressedAtUtc)
        {
            return new AiRuntimePoolCapacitySuppression
            {
                FailureId = failureId,
                PoolId = poolId,
                HostId = hostId,
                RuntimeInstanceId = runtimeInstanceId,
                RouteId = routeId,
                SuppressedAtUtc = suppressedAtUtc
            };
        }
    }
}
