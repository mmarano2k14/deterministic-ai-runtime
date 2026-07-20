using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceProviderCapabilityResolver"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceProviderCapabilityResolverTests
    {
        /// <summary>
        /// Verifies that the resolver can resolve a dispatch provider capability.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithDispatchCapability_ShouldResolveProvider()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var provider =
                TestRuntimeInstanceProviders.CreateFullLocalProvider();

            var resolver = CreateResolver(
                runtimeInstanceId,
                provider);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceDispatchProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.True(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.NotNull(resolution.Descriptor);
            Assert.Same(provider, resolution.Provider);
        }

        /// <summary>
        /// Verifies that the resolver can resolve a status provider capability.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithStatusCapability_ShouldResolveProvider()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var provider =
                TestRuntimeInstanceProviders.CreateFullLocalProvider();

            var resolver = CreateResolver(
                runtimeInstanceId,
                provider);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceStatusProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.True(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.NotNull(resolution.Descriptor);
            Assert.Same(provider, resolution.Provider);
        }

        /// <summary>
        /// Verifies that the resolver can resolve a control provider capability.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithControlCapability_ShouldResolveProvider()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var provider =
                TestRuntimeInstanceProviders.CreateFullLocalProvider();

            var resolver = CreateResolver(
                runtimeInstanceId,
                provider);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceControlProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.True(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.NotNull(resolution.Descriptor);
            Assert.Same(provider, resolution.Provider);
        }

        /// <summary>
        /// Verifies that the resolver returns a failure result when the capacity descriptor is missing.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithMissingCapacityDescriptor_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-missing";

            var capacityStore = new TestRuntimeInstanceCapacityStore();

            var router = new AiRuntimeInstanceProviderRouter(
                new[]
                {
                    TestRuntimeInstanceProviders.CreateFullLocalProvider()
                });

            

            var resolver =
                new AiRuntimeInstanceProviderCapabilityResolver(
                    capacityStore,
                    router, NullLogger<AiRuntimeInstanceProviderCapabilityResolver>.Instance);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceDispatchProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.False(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.Null(resolution.Descriptor);
            Assert.Null(resolution.Provider);
            Assert.Contains(
                runtimeInstanceId,
                resolution.FailureReason,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that the resolver returns a failure result when the provider exists
        /// but does not implement the requested capability.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithMissingCapability_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var provider =
                TestRuntimeInstanceProviders.CreateDispatchOnlyLocalProvider();

            var resolver = CreateResolver(
                runtimeInstanceId,
                provider);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceStatusProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.False(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.Null(resolution.Provider);
            Assert.NotNull(resolution.FailureReason);
        }

        /// <summary>
        /// Verifies that the resolver returns a failure result when provider metadata does not match any provider.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithUnknownProviderName_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var capacityStore = new TestRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Metadata = new Dictionary<string, string>
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "unknown"
                    }
                });

            var router = new AiRuntimeInstanceProviderRouter(
                new[]
                {
                    TestRuntimeInstanceProviders.CreateFullLocalProvider()
                });

            var resolver =
                new AiRuntimeInstanceProviderCapabilityResolver(
                    capacityStore,
                    router,
                    NullLogger<AiRuntimeInstanceProviderCapabilityResolver>.Instance);

            var resolution =
                await resolver.ResolveAsync<IAiRuntimeInstanceDispatchProvider>(
                    runtimeInstanceId,
                    CancellationToken.None);

            Assert.False(resolution.Success);
            Assert.Equal(runtimeInstanceId, resolution.RuntimeInstanceId);
            Assert.Null(resolution.Provider);
            Assert.NotNull(resolution.FailureReason);
        }

        /// <summary>
        /// Creates a resolver with one descriptor and one provider.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="provider">The runtime instance provider.</param>
        /// <returns>The provider capability resolver.</returns>
        private static AiRuntimeInstanceProviderCapabilityResolver CreateResolver(
            string runtimeInstanceId,
            IAiRuntimeInstanceProvider provider)
        {
            var capacityStore =
                new TestRuntimeInstanceCapacityStore();

            capacityStore
                .PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        Metadata = new Dictionary<string, string>
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                        }
                    })
                .GetAwaiter()
                .GetResult();

            var router =
                new AiRuntimeInstanceProviderRouter(
                    new[]
                    {
                        provider
                    });

            return new AiRuntimeInstanceProviderCapabilityResolver(
                capacityStore,
                router,
                NullLogger<AiRuntimeInstanceProviderCapabilityResolver>.Instance);
        }
    }
}