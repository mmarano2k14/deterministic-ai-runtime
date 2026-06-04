using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Runtime.Execution.MultiInstance
{
    /// <summary>
    /// Verifies that the multi-runtime instance test fixture can create isolated runtime hosts
    /// with different stable runtime instance identities.
    /// </summary>
    public sealed class AiRuntimeMultiInstanceFixtureTests
    {
        /// <summary>
        /// Verifies that each test host exposes its own deterministic runtime instance identity.
        /// </summary>
        [Fact]
        public void MultiInstanceHosts_Should_Have_Different_Runtime_Instance_Identities()
        {
            var factory = new AiRuntimeMultiInstanceTestHostFactory();

            using var hostA = factory.CreateHost("runtime-instance-a");
            using var hostB = factory.CreateHost("runtime-instance-b");
            using var hostC = factory.CreateHost("runtime-instance-c");

            Assert.Equal("runtime-instance-a", hostA.RuntimeInstanceIdentity.RuntimeInstanceId);
            Assert.Equal("runtime-instance-b", hostB.RuntimeInstanceIdentity.RuntimeInstanceId);
            Assert.Equal("runtime-instance-c", hostC.RuntimeInstanceIdentity.RuntimeInstanceId);

            Assert.NotEqual(
                hostA.RuntimeInstanceIdentity.RuntimeInstanceId,
                hostB.RuntimeInstanceIdentity.RuntimeInstanceId);

            Assert.NotEqual(
                hostA.RuntimeInstanceIdentity.RuntimeInstanceId,
                hostC.RuntimeInstanceIdentity.RuntimeInstanceId);

            Assert.NotEqual(
                hostB.RuntimeInstanceIdentity.RuntimeInstanceId,
                hostC.RuntimeInstanceIdentity.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that each host resolves the runtime instance identity from dependency injection.
        /// </summary>
        [Fact]
        public void MultiInstanceHosts_Should_Resolve_Runtime_Instance_Identity_From_DependencyInjection()
        {
            var factory = new AiRuntimeMultiInstanceTestHostFactory();

            using var hostA = factory.CreateHost("runtime-instance-a");

            var identity = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.NotNull(identity);
            Assert.Same(hostA.RuntimeInstanceIdentity, identity);
            Assert.Equal("runtime-instance-a", identity.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that each host has an isolated service provider.
        /// </summary>
        [Fact]
        public void MultiInstanceHosts_Should_Use_Different_ServiceProviders()
        {
            var factory = new AiRuntimeMultiInstanceTestHostFactory();

            using var hostA = factory.CreateHost("runtime-instance-a");
            using var hostB = factory.CreateHost("runtime-instance-b");

            Assert.NotSame(hostA.ServiceProvider, hostB.ServiceProvider);
            Assert.NotSame(hostA.RuntimeInstanceIdentity, hostB.RuntimeInstanceIdentity);
        }

        /// <summary>
        /// Verifies that the same host keeps the same runtime instance identity for its lifetime.
        /// </summary>
        [Fact]
        public void MultiInstanceHost_Should_Keep_Same_Runtime_Instance_Identity_For_Host_Lifetime()
        {
            var factory = new AiRuntimeMultiInstanceTestHostFactory();

            using var hostA = factory.CreateHost("runtime-instance-a");

            var first = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();
            var second = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.Same(first, second);
            Assert.Equal("runtime-instance-a", first.RuntimeInstanceId);
            Assert.Equal(first.RuntimeInstanceId, second.RuntimeInstanceId);
        }
    }
}