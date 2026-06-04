using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.ControlPlane.RuntimeInstances.Capacity
{
    public sealed class RedisAiRuntimeInstanceCapacityStoreTests
    {
        [Fact]
        public async Task PublishAsync_Should_Store_Capacity_Descriptor()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = new RedisAiRuntimeInstanceCapacityStore(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            var descriptor = new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 10,
                ActiveWorkerCount = 2,
                AvailableWorkerCount = 8,
                MaxWorkersPerRun = 4,
                MinWorkersRequiredPerRun = 1,
                QueuedRunCount = 1,
                RunningRunCount = 2,
                ActiveRunCount = 3,
                MaxConcurrentRuns = 5,
                MaxRunSlots = 5,
                AvailableRunSlots = 2,
                ReservedRunSlots = 1,
                EffectiveAvailableRunSlots = 1,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["environment"] = "test"
                }
            };

            await store.PublishAsync(descriptor);

            var stored = await store.GetAsync(runtimeInstanceId);

            Assert.NotNull(stored);
            Assert.Equal(runtimeInstanceId, stored.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceRole.Runtime, stored.Role);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, stored.Status);
            Assert.Equal(10, stored.WorkerCount);
            Assert.Equal(2, stored.ActiveWorkerCount);
            Assert.Equal(8, stored.AvailableWorkerCount);
            Assert.Equal(5, stored.MaxConcurrentRuns);
            Assert.Equal(5, stored.MaxRunSlots);
            Assert.Equal(2, stored.AvailableRunSlots);
            Assert.Equal(1, stored.ReservedRunSlots);
            Assert.Equal(1, stored.EffectiveAvailableRunSlots);
            Assert.True(stored.CanAcceptRun);
            Assert.Equal("test", stored.Metadata["environment"]);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Published_Descriptors()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = new RedisAiRuntimeInstanceCapacityStore(redis);

            var runtimeInstanceId1 = $"test-runtime-{Guid.NewGuid():N}";
            var runtimeInstanceId2 = $"test-runtime-{Guid.NewGuid():N}";

            await store.PublishAsync(CreateDescriptor(runtimeInstanceId1));
            await store.PublishAsync(CreateDescriptor(runtimeInstanceId2));

            var descriptors = await store.ListAsync();

            Assert.Contains(
                descriptors,
                item => item.RuntimeInstanceId == runtimeInstanceId1);

            Assert.Contains(
                descriptors,
                item => item.RuntimeInstanceId == runtimeInstanceId2);
        }

        [Fact]
        public async Task RemoveAsync_Should_Remove_Descriptor()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var store = new RedisAiRuntimeInstanceCapacityStore(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await store.PublishAsync(CreateDescriptor(runtimeInstanceId));

            var beforeRemove = await store.GetAsync(runtimeInstanceId);

            Assert.NotNull(beforeRemove);

            var removed = await store.RemoveAsync(runtimeInstanceId);

            Assert.True(removed);

            var afterRemove = await store.GetAsync(runtimeInstanceId);

            Assert.Null(afterRemove);
        }

        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 10,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 10,
                MaxWorkersPerRun = null,
                MinWorkersRequiredPerRun = 1,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                MaxConcurrentRuns = 5,
                MaxRunSlots = 5,
                AvailableRunSlots = 5,
                ReservedRunSlots = 0,
                EffectiveAvailableRunSlots = 5,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["test"] = "true"
                }
            };
        }
    }
}