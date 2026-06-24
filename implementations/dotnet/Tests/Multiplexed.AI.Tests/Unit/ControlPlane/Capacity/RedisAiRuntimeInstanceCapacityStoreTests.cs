using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Integration tests for the Redis-backed runtime instance capacity store.
    /// </summary>
    public sealed class RedisAiRuntimeInstanceCapacityStoreTests
    {
        /// <summary>
        /// Verifies that publishing a capacity descriptor persists runtime identity,
        /// tenant ownership, health, queue, worker, slot, heartbeat, and metadata fields.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Store_Capacity_Descriptor()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = CreateStore(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";
            var heartbeatAtUtc = DateTimeOffset.UtcNow;

            var descriptor = CreateDescriptor(
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                activeWorkerCount: 2,
                availableWorkerCount: 8,
                queuedRunCount: 1,
                runningRunCount: 2,
                activeRunCount: 3,
                availableRunSlots: 2,
                reservedRunSlots: 1,
                effectiveAvailableRunSlots: 1,
                heartbeatAtUtc: heartbeatAtUtc,
                metadata: new Dictionary<string, string>
                {
                    ["environment"] = "test"
                });

            await store.PublishAsync(descriptor);

            var stored = await store.GetAsync(runtimeInstanceId);

            AssertCapacityDescriptor(
                stored,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                activeWorkerCount: 2,
                availableWorkerCount: 8,
                queuedRunCount: 1,
                runningRunCount: 2,
                activeRunCount: 3,
                availableRunSlots: 2,
                reservedRunSlots: 1,
                effectiveAvailableRunSlots: 1,
                expectedMetadataKey: "environment",
                expectedMetadataValue: "test");
        }

        /// <summary>
        /// Verifies that listing capacity descriptors returns complete descriptors published
        /// for the current control plane.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Published_Descriptors()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = CreateStore(redis);

            var runtimeInstanceId1 = $"test-runtime-{Guid.NewGuid():N}";
            var runtimeInstanceId2 = $"test-runtime-{Guid.NewGuid():N}";

            await store.PublishAsync(CreateDescriptor(runtimeInstanceId1, tenantId: "tenant-1", tenantGroupId: "tenant-group-1"));
            await store.PublishAsync(CreateDescriptor(runtimeInstanceId2, tenantId: "tenant-2", tenantGroupId: "tenant-group-2"));

            var descriptors = await store.ListAsync();

            var descriptor1 = Assert.Single(descriptors.Where(item => item.RuntimeInstanceId == runtimeInstanceId1));
            var descriptor2 = Assert.Single(descriptors.Where(item => item.RuntimeInstanceId == runtimeInstanceId2));

            AssertCapacityDescriptor(descriptor1, runtimeInstanceId1, tenantId: "tenant-1", tenantGroupId: "tenant-group-1");
            AssertCapacityDescriptor(descriptor2, runtimeInstanceId2, tenantId: "tenant-2", tenantGroupId: "tenant-group-2");
        }

        /// <summary>
        /// Verifies that publishing an unhealthy descriptor preserves the health state
        /// and prevents the descriptor from reporting that it can accept new runs.
        /// </summary>
        [Fact]
        public async Task PublishAsync_Should_Store_Unhealthy_NonAccepting_Capacity_Descriptor()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = CreateStore(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await store.PublishAsync(
                CreateDescriptor(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1",
                    status: AiRuntimeInstanceStatus.Unhealthy,
                    canAcceptRun: false,
                    availableRunSlots: 0,
                    reservedRunSlots: 0,
                    effectiveAvailableRunSlots: 0,
                    metadata: new Dictionary<string, string>
                    {
                        ["healthReason"] = "heartbeat-stale"
                    }));

            var stored = await store.GetAsync(runtimeInstanceId);

            AssertCapacityDescriptor(
                stored,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                status: AiRuntimeInstanceStatus.Unhealthy,
                canAcceptRun: false,
                availableRunSlots: 0,
                reservedRunSlots: 0,
                effectiveAvailableRunSlots: 0,
                expectedMetadataKey: "healthReason",
                expectedMetadataValue: "heartbeat-stale");
        }

        /// <summary>
        /// Verifies that removing a descriptor deletes it from the capacity store.
        /// </summary>
        [Fact]
        public async Task RemoveAsync_Should_Remove_Descriptor()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = CreateStore(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await store.PublishAsync(CreateDescriptor(runtimeInstanceId));

            var beforeRemove = await store.GetAsync(runtimeInstanceId);

            Assert.NotNull(beforeRemove);

            var removed = await store.RemoveAsync(runtimeInstanceId);

            Assert.True(removed);

            var afterRemove = await store.GetAsync(runtimeInstanceId);
            var descriptors = await store.ListAsync();

            Assert.Null(afterRemove);
            Assert.DoesNotContain(descriptors, item => item.RuntimeInstanceId == runtimeInstanceId);
        }

        /// <summary>
        /// Creates a Redis-backed runtime instance capacity store for tests.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <returns>The capacity store.</returns>
        private static RedisAiRuntimeInstanceCapacityStore CreateStore(
            IConnectionMultiplexer redis)
        {
            return new RedisAiRuntimeInstanceCapacityStore(
                redis,
                Options.Create(new AiRuntimeInstanceRegistrationOptions()),
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor for Redis capacity store tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <param name="tenantGroupId">The optional tenant group identifier.</param>
        /// <param name="status">The runtime instance status.</param>
        /// <param name="canAcceptRun">Whether the runtime can accept a new run.</param>
        /// <param name="activeWorkerCount">The number of active workers.</param>
        /// <param name="availableWorkerCount">The number of available workers.</param>
        /// <param name="queuedRunCount">The number of locally queued runs.</param>
        /// <param name="runningRunCount">The number of locally running runs.</param>
        /// <param name="activeRunCount">The number of active local runs.</param>
        /// <param name="availableRunSlots">The number of available run slots.</param>
        /// <param name="reservedRunSlots">The number of reserved run slots.</param>
        /// <param name="effectiveAvailableRunSlots">The number of effectively available run slots.</param>
        /// <param name="heartbeatAtUtc">The heartbeat timestamp.</param>
        /// <param name="metadata">The descriptor metadata.</param>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId,
            string? tenantId = "tenant-1",
            string? tenantGroupId = "tenant-group-1",
            AiRuntimeInstanceStatus status = AiRuntimeInstanceStatus.Ready,
            bool canAcceptRun = true,
            int activeWorkerCount = 0,
            int availableWorkerCount = 10,
            int queuedRunCount = 0,
            int runningRunCount = 0,
            int activeRunCount = 0,
            int? availableRunSlots = 5,
            int reservedRunSlots = 0,
            int? effectiveAvailableRunSlots = 5,
            DateTimeOffset? heartbeatAtUtc = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                ControlPlaneId = "test-control-plane",
                Role = AiRuntimeInstanceRole.Runtime,
                Status = status,
                WorkerCount = 10,
                ActiveWorkerCount = activeWorkerCount,
                AvailableWorkerCount = availableWorkerCount,
                MaxWorkersPerRun = 4,
                MinWorkersRequiredPerRun = 1,
                QueuedRunCount = queuedRunCount,
                RunningRunCount = runningRunCount,
                ActiveRunCount = activeRunCount,
                MaxConcurrentRuns = 5,
                MaxRunSlots = 5,
                AvailableRunSlots = availableRunSlots,
                ReservedRunSlots = reservedRunSlots,
                EffectiveAvailableRunSlots = effectiveAvailableRunSlots,
                IsQueuePaused = false,
                CanAcceptRun = canAcceptRun,
                LastHeartbeatAtUtc = heartbeatAtUtc ?? DateTimeOffset.UtcNow,
                Metadata = metadata ?? new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            };
        }

        /// <summary>
        /// Asserts that a runtime instance capacity descriptor contains the expected runtime,
        /// tenant, health, queue, worker, slot, heartbeat, and metadata values.
        /// </summary>
        /// <param name="descriptor">The descriptor to assert.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance identifier.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        /// <param name="tenantGroupId">The expected tenant group identifier.</param>
        /// <param name="status">The expected runtime status.</param>
        /// <param name="canAcceptRun">The expected accept-run flag.</param>
        /// <param name="activeWorkerCount">The expected active worker count.</param>
        /// <param name="availableWorkerCount">The expected available worker count.</param>
        /// <param name="queuedRunCount">The expected queued run count.</param>
        /// <param name="runningRunCount">The expected running run count.</param>
        /// <param name="activeRunCount">The expected active run count.</param>
        /// <param name="availableRunSlots">The expected available run slots.</param>
        /// <param name="reservedRunSlots">The expected reserved run slots.</param>
        /// <param name="effectiveAvailableRunSlots">The expected effective available run slots.</param>
        /// <param name="expectedMetadataKey">The expected metadata key.</param>
        /// <param name="expectedMetadataValue">The expected metadata value.</param>
        private static void AssertCapacityDescriptor(
            AiRuntimeInstanceCapacityDescriptor? descriptor,
            string runtimeInstanceId,
            string? tenantId = "tenant-1",
            string? tenantGroupId = "tenant-group-1",
            AiRuntimeInstanceStatus status = AiRuntimeInstanceStatus.Ready,
            bool canAcceptRun = true,
            int activeWorkerCount = 0,
            int availableWorkerCount = 10,
            int queuedRunCount = 0,
            int runningRunCount = 0,
            int activeRunCount = 0,
            int? availableRunSlots = 5,
            int reservedRunSlots = 0,
            int? effectiveAvailableRunSlots = 5,
            string expectedMetadataKey = "test",
            string expectedMetadataValue = "true")
        {
            Assert.NotNull(descriptor);
            Assert.Equal(runtimeInstanceId, descriptor!.RuntimeInstanceId);
            Assert.Equal(tenantId, descriptor.TenantId);
            Assert.Equal(tenantGroupId, descriptor.TenantGroupId);
            Assert.Equal("test-control-plane", descriptor.ControlPlaneId);
            Assert.Equal(AiRuntimeInstanceRole.Runtime, descriptor.Role);
            Assert.Equal(status, descriptor.Status);
            Assert.Equal(10, descriptor.WorkerCount);
            Assert.Equal(activeWorkerCount, descriptor.ActiveWorkerCount);
            Assert.Equal(availableWorkerCount, descriptor.AvailableWorkerCount);
            Assert.Equal(4, descriptor.MaxWorkersPerRun);
            Assert.Equal(1, descriptor.MinWorkersRequiredPerRun);
            Assert.Equal(queuedRunCount, descriptor.QueuedRunCount);
            Assert.Equal(runningRunCount, descriptor.RunningRunCount);
            Assert.Equal(activeRunCount, descriptor.ActiveRunCount);
            Assert.Equal(5, descriptor.MaxConcurrentRuns);
            Assert.Equal(5, descriptor.MaxRunSlots);
            Assert.Equal(availableRunSlots, descriptor.AvailableRunSlots);
            Assert.Equal(reservedRunSlots, descriptor.ReservedRunSlots);
            Assert.Equal(effectiveAvailableRunSlots, descriptor.EffectiveAvailableRunSlots);
            Assert.False(descriptor.IsQueuePaused);
            Assert.Equal(canAcceptRun, descriptor.CanAcceptRun);
            Assert.True(descriptor.LastHeartbeatAtUtc > DateTimeOffset.MinValue);
            Assert.Equal(expectedMetadataValue, descriptor.Metadata[expectedMetadataKey]);
        }
    }
}