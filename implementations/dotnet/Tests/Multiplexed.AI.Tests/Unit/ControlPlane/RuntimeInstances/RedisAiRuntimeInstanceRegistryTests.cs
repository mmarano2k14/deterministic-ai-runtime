using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.ControlPlane.RuntimeInstances
{
    public sealed class RedisAiRuntimeInstanceRegistryTests
    {
        [Fact]
        public async Task RegisterAsync_Should_Create_Runtime_Instance()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            var snapshot = await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    HostName = "test-host",
                    ProcessId = 123,
                    WorkerCount = 10,
                    QueueCapacity = 100,
                    MaxConcurrentRuns = 5,
                    RuntimeVersion = "test",
                    Metadata = new Dictionary<string, string>
                    {
                        ["environment"] = "test"
                    }
                });

            Assert.Equal(runtimeInstanceId, snapshot.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceRole.Runtime, snapshot.Role);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot.Status);
            Assert.Equal(10, snapshot.WorkerCount);
            Assert.Equal(100, snapshot.QueueCapacity);
            Assert.Equal(5, snapshot.MaxConcurrentRuns);
            Assert.Equal(5, snapshot.AvailableRunSlots);
            Assert.True(snapshot.CanAcceptRun);
        }

        [Fact]
        public async Task HeartbeatAsync_Should_Update_Runtime_Instance_Capacity()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = 10,
                    QueueCapacity = 100,
                    MaxConcurrentRuns = 5
                });

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId,
                queuedRunCount: 2,
                runningRunCount: 1,
                activeRunCount: 3,
                availableRunSlots: 4,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Ready);

            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot.QueuedRunCount);
            Assert.Equal(1, snapshot.RunningRunCount);
            Assert.Equal(3, snapshot.ActiveRunCount);
            Assert.Equal(4, snapshot.AvailableRunSlots);
            Assert.True(snapshot.CanAcceptRun);
        }

        [Fact]
        public async Task HeartbeatAsync_Should_Force_ControlPlane_To_Not_Accept_Runs()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId = $"test-control-plane-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = AiRuntimeInstanceRole.ControlPlane,
                    WorkerCount = 30,
                    QueueCapacity = 100,
                    MaxConcurrentRuns = 30
                });

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId,
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 30,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Ready);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceRole.ControlPlane, snapshot.Role);
            Assert.Equal(0, snapshot.AvailableRunSlots);
            Assert.False(snapshot.CanAcceptRun);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Registered_Runtime_Instances()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId1 = $"test-runtime-{Guid.NewGuid():N}";
            var runtimeInstanceId2 = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId1,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = 10,
                    MaxConcurrentRuns = 5
                });

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId2,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = 10,
                    MaxConcurrentRuns = 5
                });

            var snapshots = await registry.ListAsync();

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId1);

            Assert.Contains(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId2);
        }

        [Fact]
        public async Task MarkDrainingAsync_Should_Mark_Runtime_Instance_As_Draining()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = 10,
                    MaxConcurrentRuns = 5
                });

            var snapshot = await registry.MarkDrainingAsync(runtimeInstanceId);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, snapshot.Status);
        }

        [Fact]
        public async Task UnregisterAsync_Should_Mark_Runtime_Instance_As_Stopped()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = new RedisAiRuntimeInstanceRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = 10,
                    MaxConcurrentRuns = 5
                });

            var snapshot = await registry.UnregisterAsync(runtimeInstanceId);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, snapshot.Status);

            var visibleSnapshots = await registry.ListAsync(includeStopped: false);

            Assert.DoesNotContain(
                visibleSnapshots,
                item => item.RuntimeInstanceId == runtimeInstanceId);

            var allSnapshots = await registry.ListAsync(includeStopped: true);

            Assert.Contains(
                allSnapshots,
                item => item.RuntimeInstanceId == runtimeInstanceId);
        }
    }
}