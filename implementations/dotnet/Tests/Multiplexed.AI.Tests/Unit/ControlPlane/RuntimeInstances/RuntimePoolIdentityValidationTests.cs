using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Validates runtime pool identity invariants at the registry boundary.
    /// </summary>
    public sealed class RuntimePoolIdentityValidationTests
    {
        /// <summary>
        /// Verifies that the current non-pooled registration model remains backward compatible.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Accept_Legacy_NonPooled_Registration()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var snapshot =
                await registry.RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId: "legacy-runtime",
                        poolId: null,
                        hostId: null));

            Assert.Null(snapshot.PoolId);
            Assert.Null(snapshot.HostId);
        }

        /// <summary>
        /// Verifies that an exact host identity may exist without runtime pool membership.
        /// </summary>
        /// <remarks>
        /// The existing Process and Kubernetes modes may publish a host incarnation without being
        /// part of the future Runtime Pool hosting mode.
        /// </remarks>
        [Fact]
        public async Task RegisterAsync_Should_Accept_HostIdentity_Without_PoolIdentity()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var snapshot =
                await registry.RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId: "current-kubernetes-runtime",
                        poolId: null,
                        hostId: "exact-host-incarnation"));

            Assert.Null(snapshot.PoolId);
            Assert.Equal("exact-host-incarnation", snapshot.HostId);
        }

        /// <summary>
        /// Verifies that a pooled runtime cannot be registered without exact host membership.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Reject_PoolIdentity_Without_HostIdentity()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            var exception =
                await Assert.ThrowsAsync<ArgumentException>(
                    () => registry.RegisterAsync(
                        CreateRegistration(
                            runtimeInstanceId: "runtime-a1",
                            poolId: "pool-shared-01",
                            hostId: null)));

            Assert.Contains("HostId", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Verifies that optional first-class identities reject whitespace-only values.
        /// </summary>
        /// <param name="poolId">The pool identity supplied to the registration.</param>
        /// <param name="hostId">The host identity supplied to the registration.</param>
        [Theory]
        [InlineData(" ", "host-a")]
        [InlineData(null, " ")]
        [InlineData("pool-shared-01", " ")]
        public async Task RegisterAsync_Should_Reject_Whitespace_FirstClassIdentity(
            string? poolId,
            string? hostId)
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();

            await Assert.ThrowsAsync<ArgumentException>(
                () => registry.RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId: "runtime-a1",
                        poolId: poolId,
                        hostId: hostId)));
        }

        /// <summary>
        /// Verifies that diagnostic metadata cannot create authoritative pool or host membership.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Not_Infer_Identity_From_Metadata()
        {
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            IAiRuntimePoolMembershipReader membershipReader = registry;

            var snapshot =
                await registry.RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId: "metadata-only-runtime",
                        poolId: null,
                        hostId: null));

            var poolMembers =
                await membershipReader.ListByPoolIdAsync("metadata-pool");

            var hostMembers =
                await membershipReader.ListByHostIdAsync("metadata-host");

            Assert.Null(snapshot.PoolId);
            Assert.Null(snapshot.HostId);
            Assert.Empty(poolMembers);
            Assert.Empty(hostMembers);
        }

        /// <summary>
        /// Creates a runtime registration used by identity validation tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="poolId">The optional logical runtime pool identifier.</param>
        /// <param name="hostId">The optional immutable host-incarnation identifier.</param>
        /// <returns>The runtime registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string? poolId,
            string? hostId)
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
                    ["pool.id"] = "metadata-pool",
                    ["host.id"] = "metadata-host"
                }
            };
        }
    }
}
