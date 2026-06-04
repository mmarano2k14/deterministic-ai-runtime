using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Runtime.Execution.Instance;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Runtime.Execution.Instance
{
    /// <summary>
    /// Tests for <see cref="DefaultAiRuntimeInstanceIdentity"/>.
    /// </summary>
    public sealed class DefaultAiRuntimeInstanceIdentityTests
    {
        /// <summary>
        /// Verifies that the default runtime instance identity creates a non-empty stable runtime instance id.
        /// </summary>
        [Fact]
        public void DefaultAiRuntimeInstanceIdentity_Should_Create_Stable_Runtime_Instance_Id()
        {
            var identity = new DefaultAiRuntimeInstanceIdentity();

            Assert.False(string.IsNullOrWhiteSpace(identity.RuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(identity.HostName));
            Assert.True(identity.ProcessId >= 0);
            Assert.True(identity.StartedAtUtc <= DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Verifies that the same identity instance keeps the same runtime instance id.
        /// </summary>
        [Fact]
        public void DefaultAiRuntimeInstanceIdentity_Should_Keep_Same_Id_For_Same_Instance()
        {
            var identity = new DefaultAiRuntimeInstanceIdentity();

            var first = identity.RuntimeInstanceId;
            var second = identity.RuntimeInstanceId;

            Assert.Equal(first, second);
        }

        /// <summary>
        /// Verifies that the runtime instance identity can be registered and resolved from dependency injection.
        /// </summary>
        [Fact]
        public void RuntimeInstanceIdentity_Should_Be_Resolvable_From_DependencyInjection()
        {
            var services = new ServiceCollection();

            services.TryAddSingleton<IAiRuntimeInstanceIdentityDescriptor, DefaultAiRuntimeInstanceIdentity>();

            using var provider = services.BuildServiceProvider();

            var identity = provider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.NotNull(identity);
            Assert.IsType<DefaultAiRuntimeInstanceIdentity>(identity);
            Assert.False(string.IsNullOrWhiteSpace(identity.RuntimeInstanceId));
        }

        /// <summary>
        /// Verifies that the runtime instance identity is registered as a singleton.
        /// </summary>
        [Fact]
        public void RuntimeInstanceIdentity_Should_Be_Singleton()
        {
            var services = new ServiceCollection();

            services.TryAddSingleton<IAiRuntimeInstanceIdentityDescriptor, DefaultAiRuntimeInstanceIdentity>();

            using var provider = services.BuildServiceProvider();

            var first = provider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();
            var second = provider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.Same(first, second);
            Assert.Equal(first.RuntimeInstanceId, second.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that a test runtime instance identity can override the default registration.
        /// </summary>
        [Fact]
        public void RuntimeInstanceIdentity_Should_Allow_Test_Override()
        {
            var services = new ServiceCollection();

            services.TryAddSingleton<IAiRuntimeInstanceIdentityDescriptor, DefaultAiRuntimeInstanceIdentity>();

            services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
            services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                new TestAiRuntimeInstanceIdentity("runtime-instance-a"));

            using var provider = services.BuildServiceProvider();

            var identity = provider.GetRequiredService<IAiRuntimeInstanceIdentityDescriptor>();

            Assert.IsType<TestAiRuntimeInstanceIdentity>(identity);
            Assert.Equal("runtime-instance-a", identity.RuntimeInstanceId);
        }
    }
}