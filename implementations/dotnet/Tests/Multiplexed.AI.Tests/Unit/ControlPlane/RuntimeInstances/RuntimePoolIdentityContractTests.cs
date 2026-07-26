using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using System.Text.Json;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Validates the first-class runtime pool and host identity contract.
    /// </summary>
    public sealed class RuntimePoolIdentityContractTests
    {
        /// <summary>
        /// Verifies that the in-memory registry preserves authoritative identity through lifecycle transitions.
        /// </summary>
        [Fact]
        public async Task InMemoryRegistry_Should_Preserve_FirstClassIdentity_Across_LifecycleTransitions()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var registered = await registry.RegisterAsync(
                CreateRegistration(
                    poolId: "pool-shared-01",
                    hostId: "host-incarnation-a",
                    runtimeId: "runtime-a",
                    controlPlaneHostId: "control-plane-host-01",
                    controlPlaneId: "control-plane-01"));

            AssertIdentity(registered);

            var heartbeat = await registry.HeartbeatAsync(
                runtimeInstanceId: "runtime-instance-a1",
                queuedRunCount: 1,
                runningRunCount: 1,
                activeRunCount: 1,
                availableRunSlots: 1,
                activeWorkerCount: 1,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy);

            Assert.NotNull(heartbeat);
            AssertIdentity(heartbeat);

            var draining = await registry.MarkDrainingAsync("runtime-instance-a1");

            Assert.NotNull(draining);
            AssertIdentity(draining);
            Assert.Equal(AiRuntimeInstanceStatus.Draining, draining.Status);
            Assert.False(draining.CanAcceptRun);

            var reRegistered = await registry.RegisterAsync(
                CreateRegistration(
                    poolId: null,
                    hostId: null,
                    runtimeId: null,
                    controlPlaneHostId: null,
                    controlPlaneId: null));

            AssertIdentity(reRegistered);
        }

        /// <summary>
        /// Verifies that the persisted registry entry preserves identity through heartbeat and status projections.
        /// </summary>
        [Fact]
        public void RuntimeInstanceEntry_Should_Preserve_FirstClassIdentity_Across_Projections()
        {
            var now = DateTimeOffset.UtcNow;
            var entry = RuntimeInstanceEntry.Create(
                CreateRegistration(
                    poolId: "pool-shared-01",
                    hostId: "host-incarnation-a",
                    runtimeId: "runtime-a",
                    controlPlaneHostId: "control-plane-host-01",
                    controlPlaneId: "control-plane-01"),
                now);

            var heartbeat = entry.UpdateHeartbeat(
                queuedRunCount: 1,
                runningRunCount: 1,
                activeRunCount: 1,
                availableRunSlots: 1,
                activeWorkerCount: 1,
                availableWorkerCount: 1,
                maxLocalWorkersPerExecution: 1,
                isQueuePaused: false,
                canAcceptRun: true,
                status: AiRuntimeInstanceStatus.Busy,
                now: now.AddSeconds(1));

            var draining = heartbeat.WithStatus(
                AiRuntimeInstanceStatus.Draining,
                now.AddSeconds(2));

            AssertIdentity(draining.ToSnapshot(now.AddSeconds(3)));
        }

        /// <summary>
        /// Verifies JSON compatibility for current and legacy registry entries.
        /// </summary>
        [Fact]
        public void RuntimeInstanceEntry_Should_RoundTrip_NewIdentity_And_Deserialize_LegacyPayload()
        {
            var now = DateTimeOffset.UtcNow;
            var entry = RuntimeInstanceEntry.Create(
                CreateRegistration(
                    poolId: "pool-shared-01",
                    hostId: "host-incarnation-a",
                    runtimeId: "runtime-a",
                    controlPlaneHostId: "control-plane-host-01",
                    controlPlaneId: "control-plane-01"),
                now);

            var serialized = JsonSerializer.Serialize(entry);
            var roundTripped = JsonSerializer.Deserialize<RuntimeInstanceEntry>(serialized);

            Assert.NotNull(roundTripped);
            Assert.Equal("pool-shared-01", roundTripped.PoolId);
            Assert.Equal("host-incarnation-a", roundTripped.HostId);

            const string legacyJson =
                "{\"RuntimeInstanceId\":\"legacy-runtime\",\"Role\":1,\"Status\":1}";

            var legacy = JsonSerializer.Deserialize<RuntimeInstanceEntry>(legacyJson);

            Assert.NotNull(legacy);
            Assert.Null(legacy.PoolId);
            Assert.Null(legacy.HostId);
        }

        /// <summary>
        /// Verifies that capacity exposes authoritative pool and host identities as typed properties.
        /// </summary>
        [Fact]
        public void CapacityDescriptor_Should_Expose_FirstClassPoolAndHostIdentity()
        {
            var descriptor = new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "runtime-instance-a1",
                PoolId = "pool-shared-01",
                HostId = "host-incarnation-a",
                Metadata = new Dictionary<string, string>
                {
                    ["pool.id"] = "diagnostic-value-must-not-be-authoritative",
                    ["host.id"] = "diagnostic-value-must-not-be-authoritative"
                }
            };

            Assert.Equal("pool-shared-01", descriptor.PoolId);
            Assert.Equal("host-incarnation-a", descriptor.HostId);
            Assert.NotEqual(descriptor.PoolId, descriptor.Metadata["pool.id"]);
            Assert.NotEqual(descriptor.HostId, descriptor.Metadata["host.id"]);
        }

        /// <summary>
        /// Creates a runtime instance registration for identity contract validation.
        /// </summary>
        /// <param name="poolId">The runtime pool identifier.</param>
        /// <param name="hostId">The immutable host-incarnation identifier.</param>
        /// <param name="runtimeId">The logical runtime identifier.</param>
        /// <param name="controlPlaneHostId">The control-plane host identifier.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string? poolId,
            string? hostId,
            string? runtimeId,
            string? controlPlaneHostId,
            string? controlPlaneId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-instance-a1",
                PoolId = poolId,
                HostId = hostId,
                RuntimeId = runtimeId,
                ControlPlaneHostId = controlPlaneHostId,
                ControlPlaneId = controlPlaneId,
                Role = AiRuntimeInstanceRole.Runtime,
                TenantId = "tenant-a",
                WorkerCount = 2,
                MaxConcurrentRuns = 2,
                QueueCapacity = 4,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["pool.id"] = "diagnostic-value-must-not-be-authoritative",
                    ["host.id"] = "diagnostic-value-must-not-be-authoritative"
                }
            };
        }

        /// <summary>
        /// Asserts the authoritative runtime pool identity fields on a snapshot.
        /// </summary>
        /// <param name="snapshot">The runtime instance snapshot.</param>
        private static void AssertIdentity(
            AiRuntimeInstanceSnapshot snapshot)
        {
            Assert.Equal("pool-shared-01", snapshot.PoolId);
            Assert.Equal("host-incarnation-a", snapshot.HostId);
            Assert.Equal("runtime-a", snapshot.RuntimeId);
            Assert.Equal("control-plane-host-01", snapshot.ControlPlaneHostId);
            Assert.Equal("control-plane-01", snapshot.ControlPlaneId);
            Assert.Equal("runtime-instance-a1", snapshot.RuntimeInstanceId);
            Assert.NotEqual(snapshot.PoolId, snapshot.Metadata["pool.id"]);
            Assert.NotEqual(snapshot.HostId, snapshot.Metadata["host.id"]);
        }
    }
}
