using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    public sealed class InMemoryAiRuntimeInstanceRegistryTests
    {
        [Fact]
        public async Task RegisterAsync_Should_Register_Runtime_Instance()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var snapshot = await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                HostName = "host-1",
                ProcessId = 1234,
                WorkerCount = 4,
                QueueCapacity = 8,
                MaxConcurrentRuns = 2,
                RuntimeVersion = "1.0.0",
                Metadata = new Dictionary<string, string>
                {
                    ["zone"] = "local"
                }
            });

            Assert.Equal("runtime-1", snapshot.RuntimeInstanceId);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot.Status);
            Assert.Equal("host-1", snapshot.HostName);
            Assert.Equal(1234, snapshot.ProcessId);
            Assert.Equal(4, snapshot.WorkerCount);
            Assert.Equal(8, snapshot.QueueCapacity);
            Assert.Equal(2, snapshot.MaxConcurrentRuns);
            Assert.Equal(2, snapshot.AvailableRunSlots);
            Assert.True(snapshot.CanAcceptRun);
            Assert.Equal("1.0.0", snapshot.RuntimeVersion);
            Assert.Equal("local", snapshot.Metadata["zone"]);
        }

        [Fact]
        public async Task GetAsync_Should_Return_Registered_Instance()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            var snapshot = await registry.GetAsync("runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal("runtime-1", snapshot!.RuntimeInstanceId);
        }

        [Fact]
        public async Task GetAsync_Should_Return_Null_When_Instance_Is_Unknown()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var snapshot = await registry.GetAsync("missing-runtime");

            Assert.Null(snapshot);
        }

        [Fact]
        public async Task HeartbeatAsync_Should_Update_Queue_And_Status_State()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 4,
                QueueCapacity = 8,
                MaxConcurrentRuns = 2
            });

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-1",
                queuedRunCount: 3,
                runningRunCount: 2,
                activeRunCount: 5,
                availableRunSlots: 0,
                activeWorkerCount: 4,
                availableWorkerCount: 0,
                maxLocalWorkersPerExecution: 2,
                isQueuePaused: false,
                canAcceptRun: false,
                status: AiRuntimeInstanceStatus.Busy);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Busy, snapshot!.Status);
            Assert.Equal(3, snapshot.QueuedRunCount);
            Assert.Equal(2, snapshot.RunningRunCount);
            Assert.Equal(5, snapshot.ActiveRunCount);
            Assert.Equal(0, snapshot.AvailableRunSlots);
            Assert.False(snapshot.IsQueuePaused);
            Assert.False(snapshot.CanAcceptRun);
        }

        [Fact]
        public async Task HeartbeatAsync_Should_Return_Null_When_Instance_Is_Unknown()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId: "missing-runtime",
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 1,
                activeWorkerCount: 0,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Ready);

            Assert.Null(snapshot);
        }

        [Fact]
        public async Task ListAsync_Should_Return_Registered_Instances_Ordered_By_RuntimeInstanceId()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-b",
                WorkerCount = 1
            });

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-a",
                WorkerCount = 1
            });

            var snapshots = await registry.ListAsync();

            Assert.Equal(2, snapshots.Count);
            Assert.Equal("runtime-a", snapshots[0].RuntimeInstanceId);
            Assert.Equal("runtime-b", snapshots[1].RuntimeInstanceId);
        }

        [Fact]
        public async Task MarkDrainingAsync_Should_Update_Status()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            var snapshot = await registry.MarkDrainingAsync("runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, snapshot!.Status);
        }

        [Fact]
        public async Task UnregisterAsync_Should_Mark_Instance_As_Stopped()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            var snapshot = await registry.UnregisterAsync("runtime-1");

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, snapshot!.Status);
        }

        [Fact]
        public async Task ListAsync_Should_Exclude_Stopped_Instances_By_Default()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            await registry.UnregisterAsync("runtime-1");

            var snapshots = await registry.ListAsync();

            Assert.Empty(snapshots);
        }

        [Fact]
        public async Task ListAsync_Should_Include_Stopped_Instances_When_Requested()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            await registry.UnregisterAsync("runtime-1");

            var snapshots = await registry.ListAsync(includeStopped: true);

            Assert.Single(snapshots);
            Assert.Equal(AiRuntimeInstanceStatus.Stopped, snapshots[0].Status);
        }

        [Fact]
        public async Task RegisterAsync_Should_Reactivate_Stopped_Instance()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 2
            });

            await registry.UnregisterAsync("runtime-1");

            var snapshot = await registry.RegisterAsync(new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                WorkerCount = 3
            });

            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot.Status);
            Assert.Equal(3, snapshot.WorkerCount);
        }
    }
}