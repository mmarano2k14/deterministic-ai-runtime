using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Validates first-class runtime pool and host registration identity resolution.
    /// </summary>
    public sealed class RuntimeInstanceRegistrationIdentityResolverTests
    {
        /// <summary>
        /// Verifies that explicitly configured process-pool identities remain authoritative.
        /// </summary>
        [Fact]
        public void Resolve_Should_Prefer_Configured_HostIdentity()
        {
            var identity =
                AiRuntimeInstanceRegistrationIdentityResolver.Resolve(
                    "pool-01",
                    "pool-host-incarnation-01",
                    "environment-host");

            Assert.Equal("pool-01", identity.PoolId);
            Assert.Equal("pool-host-incarnation-01", identity.HostId);
        }

        /// <summary>
        /// Verifies backward-compatible fallback to the provider-neutral environment host.
        /// </summary>
        [Fact]
        public void Resolve_Should_Fallback_To_EnvironmentHost()
        {
            var identity =
                AiRuntimeInstanceRegistrationIdentityResolver.Resolve(
                    null,
                    null,
                    "existing-host");

            Assert.Null(identity.PoolId);
            Assert.Equal("existing-host", identity.HostId);
        }

        /// <summary>
        /// Verifies that pooled registration cannot exist without an exact host incarnation.
        /// </summary>
        [Fact]
        public void Resolve_Should_Reject_Pool_Without_Host()
        {
            var exception =
                Assert.Throws<InvalidOperationException>(
                    () => AiRuntimeInstanceRegistrationIdentityResolver.Resolve(
                        "pool-01",
                        null,
                        null));

            Assert.Contains("HostId", exception.Message, StringComparison.Ordinal);
        }
    }
}
