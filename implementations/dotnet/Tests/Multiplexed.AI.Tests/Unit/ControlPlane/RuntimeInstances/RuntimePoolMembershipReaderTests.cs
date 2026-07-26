using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Validates first-class runtime pool and host membership queries.
    /// </summary>
    public sealed class RuntimePoolMembershipReaderTests
    {
        /// <summary>
        /// Verifies that pool and host membership use typed identity instead of optional metadata.
        /// </summary>
        [Fact]
        public async Task InMemoryRegistry_Should_Query_Membership_From_FirstClassIdentity()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            IAiRuntimePoolMembershipReader membershipReader = registry;

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-a1",
                    poolId: "pool-shared-01",
                    hostId: "host-a"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-a2",
                    poolId: "pool-shared-01",
                    hostId: "host-a"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-b1",
                    poolId: "pool-shared-01",
                    hostId: "host-b"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-c1",
                    poolId: "pool-dedicated-01",
                    hostId: "host-c"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-metadata-only",
                    poolId: null,
                    hostId: null,
                    metadataPoolId: "pool-shared-01",
                    metadataHostId: "host-a"));

            await registry.MarkDrainingAsync("runtime-a1");
            await registry.MarkUnhealthyAsync("runtime-a2");

            var poolMembers =
                await membershipReader.ListByPoolIdAsync("pool-shared-01");

            Assert.Equal(
                new[]
                {
                    "runtime-a1",
                    "runtime-a2",
                    "runtime-b1"
                },
                poolMembers.Select(member => member.RuntimeInstanceId));

            var hostMembers =
                await membershipReader.ListByHostIdAsync("host-a");

            Assert.Equal(
                new[]
                {
                    "runtime-a1",
                    "runtime-a2"
                },
                hostMembers.Select(member => member.RuntimeInstanceId));

            var hostIds =
                await membershipReader.ListHostIdsByPoolIdAsync("pool-shared-01");

            Assert.Equal(
                new[]
                {
                    "host-a",
                    "host-b"
                },
                hostIds);
        }

        /// <summary>
        /// Verifies that stopped runtime instances leave the active membership projection.
        /// </summary>
        [Fact]
        public async Task InMemoryRegistry_Should_Exclude_Stopped_Runtime_From_ActiveMembership()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            IAiRuntimePoolMembershipReader membershipReader = registry;

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-a1",
                    poolId: "pool-shared-01",
                    hostId: "host-a"));

            await registry.RegisterAsync(
                CreateRegistration(
                    runtimeInstanceId: "runtime-a2",
                    poolId: "pool-shared-01",
                    hostId: "host-a"));

            await registry.UnregisterAsync("runtime-a2");

            var poolMembers =
                await membershipReader.ListByPoolIdAsync("pool-shared-01");

            var hostMembers =
                await membershipReader.ListByHostIdAsync("host-a");

            Assert.Single(poolMembers);
            Assert.Equal("runtime-a1", poolMembers[0].RuntimeInstanceId);
            Assert.Single(hostMembers);
            Assert.Equal("runtime-a1", hostMembers[0].RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that membership identities are validated before querying.
        /// </summary>
        [Fact]
        public async Task MembershipReader_Should_Reject_Empty_Identity()
        {
            IAiRuntimePoolMembershipReader membershipReader =
                new InMemoryAiRuntimeInstanceRegistry();

            await Assert.ThrowsAsync<ArgumentException>(
                () => membershipReader.ListByPoolIdAsync(" "));

            await Assert.ThrowsAsync<ArgumentException>(
                () => membershipReader.ListByHostIdAsync(" "));

            await Assert.ThrowsAsync<ArgumentException>(
                () => membershipReader.ListHostIdsByPoolIdAsync(" "));
        }

        /// <summary>
        /// Verifies that both production registries expose the same membership contract.
        /// </summary>
        [Fact]
        public void ProductionRegistries_Should_Implement_RuntimePoolMembershipReader()
        {
            Assert.True(
                typeof(IAiRuntimePoolMembershipReader)
                    .IsAssignableFrom(typeof(InMemoryAiRuntimeInstanceRegistry)));

            Assert.True(
                typeof(IAiRuntimePoolMembershipReader)
                    .IsAssignableFrom(typeof(RedisAiRuntimeInstanceRegistry)));
        }

        /// <summary>
        /// Creates a runtime registration for membership validation.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="poolId">The typed logical pool identifier.</param>
        /// <param name="hostId">The typed immutable host-incarnation identifier.</param>
        /// <param name="metadataPoolId">The optional diagnostic pool metadata value.</param>
        /// <param name="metadataHostId">The optional diagnostic host metadata value.</param>
        /// <returns>The runtime registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string? poolId,
            string? hostId,
            string metadataPoolId = "diagnostic-pool",
            string metadataHostId = "diagnostic-host")
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = poolId,
                HostId = hostId,
                RuntimeId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                WorkerCount = 2,
                MaxConcurrentRuns = 2,
                QueueCapacity = 4,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["pool.id"] = metadataPoolId,
                    ["host.id"] = metadataHostId
                }
            };
        }
    }
}
