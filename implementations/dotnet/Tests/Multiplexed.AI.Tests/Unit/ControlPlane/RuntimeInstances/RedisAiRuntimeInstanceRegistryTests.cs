using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Integration tests for the Redis-backed runtime instance registry.
    /// </summary>
    public sealed class RedisAiRuntimeInstanceRegistryTests
    {
        /// <summary>
        /// Verifies that registering a runtime instance persists identity, tenant ownership,
        /// role, capacity, diagnostics, control-plane ownership, and metadata.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Create_Runtime_Instance()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            var snapshot = await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1",
                    hostName: "test-host",
                    processId: 123,
                    workerCount: 10,
                    queueCapacity: 100,
                    maxConcurrentRuns: 5,
                    runtimeVersion: "test",
                    metadata: new Dictionary<string, string>
                    {
                        ["environment"] = "test"
                    }));

            AssertRuntimeSnapshot(
                snapshot,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                hostName: "test-host",
                processId: 123,
                workerCount: 10,
                queueCapacity: 100,
                maxConcurrentRuns: 5,
                availableRunSlots: 5,
                activeWorkerCount: 0,
                availableWorkerCount: 10,
                runtimeVersion: "test",
                expectedMetadataKey: "environment",
                expectedMetadataValue: "test");
        }

        /// <summary>
        /// Verifies that a registered runtime instance can be loaded directly from Redis.
        /// </summary>
        [Fact]
        public async Task GetAsync_Should_Return_Registered_Runtime_Instance()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1",
                    metadata: new Dictionary<string, string>
                    {
                        ["environment"] = "test"
                    }));

            var snapshot = await registry.GetAsync(runtimeInstanceId);

            AssertRuntimeSnapshot(
                snapshot,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                activeWorkerCount: 0,
                availableWorkerCount: 10,
                expectedMetadataKey: "environment",
                expectedMetadataValue: "test");
        }

        /// <summary>
        /// Verifies that heartbeat updates runtime queue, run, worker, slot, accept-run,
        /// heartbeat, and status fields while preserving identity and tenant ownership.
        /// </summary>
        [Fact]
        public async Task HeartbeatAsync_Should_Update_Runtime_Instance_Capacity()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            var registered = await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1",
                    workerCount: 10,
                    queueCapacity: 100,
                    maxConcurrentRuns: 5));

            Assert.Equal("tenant-1", registered.TenantId);
            Assert.Equal("tenant-group-1", registered.TenantGroupId);

            var loadedBeforeHeartbeat = await registry.GetAsync(runtimeInstanceId);

            Assert.NotNull(loadedBeforeHeartbeat);
            Assert.Equal("tenant-1", loadedBeforeHeartbeat!.TenantId);
            Assert.Equal("tenant-group-1", loadedBeforeHeartbeat.TenantGroupId);

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId,
                queuedRunCount: 2,
                runningRunCount: 1,
                activeRunCount: 3,
                availableRunSlots: 4,
                activeWorkerCount: 6,
                availableWorkerCount: 4,
                maxLocalWorkersPerExecution: 2,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Ready);

            Assert.NotNull(snapshot);
            Assert.Equal("tenant-1", snapshot!.TenantId);
            Assert.Equal("tenant-group-1", snapshot.TenantGroupId);

            var loadedAfterHeartbeat = await registry.GetAsync(runtimeInstanceId);

            Assert.NotNull(loadedAfterHeartbeat);
            Assert.Equal("tenant-1", loadedAfterHeartbeat!.TenantId);
            Assert.Equal("tenant-group-1", loadedAfterHeartbeat.TenantGroupId);

            AssertRuntimeSnapshot(
                snapshot,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                queuedRunCount: 2,
                runningRunCount: 1,
                activeRunCount: 3,
                availableRunSlots: 4,
                activeWorkerCount: 6,
                availableWorkerCount: 4,
                maxLocalWorkersPerExecution: 2);
        }

        /// <summary>
        /// Verifies that a control-plane registration is never exposed as dispatchable capacity,
        /// even when heartbeat reports available slots.
        /// </summary>
        [Fact]
        public async Task HeartbeatAsync_Should_Force_ControlPlane_To_Not_Accept_Runs()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-control-plane-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    role: AiRuntimeInstanceRole.ControlPlane,
                    workerCount: 30,
                    queueCapacity: 100,
                    maxConcurrentRuns: 30));

            var snapshot = await registry.HeartbeatAsync(
                runtimeInstanceId,
                queuedRunCount: 0,
                runningRunCount: 0,
                activeRunCount: 0,
                availableRunSlots: 30,
                activeWorkerCount: 0,
                availableWorkerCount: 30,
                maxLocalWorkersPerExecution: 5,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Ready);

            Assert.NotNull(snapshot);
            Assert.Equal(AiRuntimeInstanceRole.ControlPlane, snapshot!.Role);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, snapshot.Status);
            Assert.Equal(0, snapshot.AvailableRunSlots);
            Assert.False(snapshot.CanAcceptRun);
        }

        /// <summary>
        /// Verifies that listing runtime instances returns complete registered snapshots.
        /// </summary>
        [Fact]
        public async Task ListAsync_Should_Return_Registered_Runtime_Instances()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId1 = $"test-runtime-{Guid.NewGuid():N}";
            var runtimeInstanceId2 = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(CreateRegistration(runtimeInstanceId1, tenantId: "tenant-1", tenantGroupId: "tenant-group-1"));
            await registry.RegisterAsync(CreateRegistration(runtimeInstanceId2, tenantId: "tenant-2", tenantGroupId: "tenant-group-2"));

            var snapshots = await registry.ListAsync();

            var snapshot1 = Assert.Single(snapshots.Where(snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId1));
            var snapshot2 = Assert.Single(snapshots.Where(snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId2));

            AssertRuntimeSnapshot(snapshot1, runtimeInstanceId1, tenantId: "tenant-1", tenantGroupId: "tenant-group-1", activeWorkerCount: 0, availableWorkerCount: 10);
            AssertRuntimeSnapshot(snapshot2, runtimeInstanceId2, tenantId: "tenant-2", tenantGroupId: "tenant-group-2", activeWorkerCount: 0, availableWorkerCount: 10);
        }

        /// <summary>
        /// Verifies that marking a runtime instance as draining preserves identity,
        /// tenant ownership, and runtime metadata while changing status.
        /// </summary>
        [Fact]
        public async Task MarkDrainingAsync_Should_Mark_Runtime_Instance_As_Draining()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1"));

            var snapshot = await registry.MarkDrainingAsync(runtimeInstanceId);

            AssertRuntimeSnapshot(
                snapshot,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                status: AiRuntimeInstanceStatus.Draining,
                availableRunSlots: 5,
                canAcceptRun: false,
                activeWorkerCount: 0,
                availableWorkerCount: 10);
        }

        /// <summary>
        /// Verifies that unregistering a runtime instance marks it as stopped and removes
        /// it from visible registry listings.
        /// </summary>
        [Fact]
        public async Task UnregisterAsync_Should_Remove_Runtime_Instance_From_Registry()
        {
            var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
            var registry = CreateRegistry(redis);

            var runtimeInstanceId = $"test-runtime-{Guid.NewGuid():N}";

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId,
                    tenantId: "tenant-1",
                    tenantGroupId: "tenant-group-1"));

            var snapshot = await registry.UnregisterAsync(runtimeInstanceId);

            AssertRuntimeSnapshot(
                snapshot,
                runtimeInstanceId,
                tenantId: "tenant-1",
                tenantGroupId: "tenant-group-1",
                status: AiRuntimeInstanceStatus.Stopped,
                availableRunSlots: 5,
                canAcceptRun: false,
                activeWorkerCount: 0,
                availableWorkerCount: 10);

            var visibleSnapshots = await registry.ListAsync(includeStopped: false);

            Assert.DoesNotContain(visibleSnapshots, item => item.RuntimeInstanceId == runtimeInstanceId);

            var allSnapshots = await registry.ListAsync(includeStopped: true);

            Assert.DoesNotContain(allSnapshots, item => item.RuntimeInstanceId == runtimeInstanceId);
        }

        /// <summary>
        /// Creates a Redis-backed runtime instance registry for tests.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <returns>The runtime instance registry.</returns>
        private static RedisAiRuntimeInstanceRegistry CreateRegistry(
            IConnectionMultiplexer redis)
        {
            return new RedisAiRuntimeInstanceRegistry(
                redis,
                Options.Create(new AiRuntimeInstanceRegistrationOptions()),
                new StaticAiControlPlaneIdResolver("test-control-plane"));
        }

        /// <summary>
        /// Creates a runtime instance registration for Redis registry tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenantId">The optional tenant identifier.</param>
        /// <param name="tenantGroupId">The optional tenant group identifier.</param>
        /// <param name="role">The runtime instance role.</param>
        /// <param name="hostName">The optional host name.</param>
        /// <param name="processId">The optional process identifier.</param>
        /// <param name="workerCount">The worker count.</param>
        /// <param name="queueCapacity">The local queue capacity.</param>
        /// <param name="maxConcurrentRuns">The maximum concurrent runs.</param>
        /// <param name="runtimeVersion">The optional runtime version.</param>
        /// <param name="metadata">The registration metadata.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string? tenantId = "tenant-1",
            string? tenantGroupId = "tenant-group-1",
            AiRuntimeInstanceRole role = AiRuntimeInstanceRole.Runtime,
            string? hostName = "test-host",
            int? processId = 123,
            int workerCount = 10,
            int? queueCapacity = 100,
            int? maxConcurrentRuns = 5,
            string? runtimeVersion = "test",
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                ControlPlaneId = "test-control-plane",
                Role = role,
                HostId = "test-host-id",
                RuntimeId = "test-runtime-id",
                ControlPlaneHostId = "test-control-plane-host",
                HostName = hostName,
                ProcessId = processId,
                WorkerCount = workerCount,
                QueueCapacity = queueCapacity,
                MaxConcurrentRuns = maxConcurrentRuns,
                RuntimeVersion = runtimeVersion,
                Metadata = metadata ?? new Dictionary<string, string>
                {
                    ["test"] = "true"
                },
                RegisteredAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Asserts that a runtime instance snapshot contains the expected identity,
        /// tenant ownership, role, status, capacity, heartbeat, diagnostics, and metadata fields.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance identifier.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        /// <param name="tenantGroupId">The expected tenant group identifier.</param>
        /// <param name="status">The expected runtime status.</param>
        /// <param name="role">The expected runtime role.</param>
        /// <param name="hostName">The expected host name.</param>
        /// <param name="processId">The expected process identifier.</param>
        /// <param name="workerCount">The expected worker count.</param>
        /// <param name="queueCapacity">The expected queue capacity.</param>
        /// <param name="maxConcurrentRuns">The expected maximum concurrent runs.</param>
        /// <param name="queuedRunCount">The expected queued run count.</param>
        /// <param name="runningRunCount">The expected running run count.</param>
        /// <param name="activeRunCount">The expected active run count.</param>
        /// <param name="availableRunSlots">The expected available run slots.</param>
        /// <param name="isQueuePaused">The expected queue paused flag.</param>
        /// <param name="canAcceptRun">The expected accept-run flag.</param>
        /// <param name="activeWorkerCount">The expected active worker count.</param>
        /// <param name="availableWorkerCount">The expected available worker count.</param>
        /// <param name="maxLocalWorkersPerExecution">The expected max workers per execution.</param>
        /// <param name="runtimeVersion">The expected runtime version.</param>
        /// <param name="expectedMetadataKey">The expected metadata key.</param>
        /// <param name="expectedMetadataValue">The expected metadata value.</param>
        private static void AssertRuntimeSnapshot(
            AiRuntimeInstanceSnapshot? snapshot,
            string runtimeInstanceId,
            string? tenantId = "tenant-1",
            string? tenantGroupId = "tenant-group-1",
            AiRuntimeInstanceStatus status = AiRuntimeInstanceStatus.Ready,
            AiRuntimeInstanceRole role = AiRuntimeInstanceRole.Runtime,
            string? hostName = "test-host",
            int? processId = 123,
            int workerCount = 10,
            int? queueCapacity = 100,
            int? maxConcurrentRuns = 5,
            int queuedRunCount = 0,
            int runningRunCount = 0,
            int activeRunCount = 0,
            int? availableRunSlots = 5,
            bool isQueuePaused = false,
            bool canAcceptRun = true,
            int? activeWorkerCount = 0,
            int? availableWorkerCount = 10,
            int? maxLocalWorkersPerExecution = null,
            string? runtimeVersion = "test",
            string expectedMetadataKey = "test",
            string expectedMetadataValue = "true")
        {
            Assert.NotNull(snapshot);
            Assert.Equal(runtimeInstanceId, snapshot!.RuntimeInstanceId);
            Assert.Equal(tenantId, snapshot.TenantId);
            Assert.Equal(tenantGroupId, snapshot.TenantGroupId);
            Assert.Equal("test-control-plane", snapshot.ControlPlaneId);
            Assert.Equal("test-control-plane-host", snapshot.ControlPlaneHostId);
            Assert.Equal("test-host-id", snapshot.HostId);
            Assert.Equal("test-runtime-id", snapshot.RuntimeId);
            Assert.Equal(role, snapshot.Role);
            Assert.Equal(status, snapshot.Status);
            Assert.Equal(hostName, snapshot.HostName);
            Assert.Equal(processId, snapshot.ProcessId);
            Assert.Equal(workerCount, snapshot.WorkerCount);
            Assert.Equal(queueCapacity, snapshot.QueueCapacity);
            Assert.Equal(maxConcurrentRuns, snapshot.MaxConcurrentRuns);
            Assert.Equal(queuedRunCount, snapshot.QueuedRunCount);
            Assert.Equal(runningRunCount, snapshot.RunningRunCount);
            Assert.Equal(activeRunCount, snapshot.ActiveRunCount);
            Assert.Equal(availableRunSlots, snapshot.AvailableRunSlots);
            Assert.Equal(isQueuePaused, snapshot.IsQueuePaused);
            Assert.Equal(canAcceptRun, snapshot.CanAcceptRun);
            Assert.Equal(activeWorkerCount, snapshot.ActiveWorkerCount);
            Assert.Equal(availableWorkerCount, snapshot.AvailableWorkerCount);
            Assert.Equal(maxLocalWorkersPerExecution, snapshot.MaxLocalWorkersPerExecution);
            Assert.Equal(runtimeVersion, snapshot.RuntimeVersion);
            Assert.True(snapshot.RegisteredAtUtc > DateTimeOffset.MinValue);
            Assert.True(snapshot.LastHeartbeatAtUtc > DateTimeOffset.MinValue);
            Assert.True(snapshot.SnapshotAtUtc > DateTimeOffset.MinValue);
            Assert.Equal(expectedMetadataValue, snapshot.Metadata[expectedMetadataKey]);
        }
    }
}