using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.RuntimeInstances.Providers
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

            var provider = new TestFullProvider();

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

            var provider = new TestFullProvider();

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

            var provider = new TestFullProvider();

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
                new IAiRuntimeInstanceProvider[]
                {
                    new TestFullProvider()
                });

            var resolver =
                new AiRuntimeInstanceProviderCapabilityResolver(
                    capacityStore,
                    router);

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
        /// Verifies that the resolver returns a failure result when the provider exists but not for the requested capability.
        /// </summary>
        [Fact]
        public async Task ResolveAsync_WithMissingCapability_ShouldReturnFailure()
        {
            var runtimeInstanceId = "mcp-runtime-1";

            var provider = new TestDispatchOnlyProvider();

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
                new IAiRuntimeInstanceProvider[]
                {
                    new TestFullProvider()
                });

            var resolver =
                new AiRuntimeInstanceProviderCapabilityResolver(
                    capacityStore,
                    router);

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
                router);
        }

        /// <summary>
        /// Test runtime instance capacity store.
        /// </summary>
        private sealed class TestRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors =
                new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(descriptor);

                descriptors[descriptor.RuntimeInstanceId] = descriptor;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                descriptors.TryGetValue(
                    runtimeInstanceId,
                    out var descriptor);

                return Task.FromResult(descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                    descriptors.Values.ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                return Task.FromResult(
                    descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Test provider implementing dispatch, status, and control capabilities.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class TestFullProvider :
            IAiRuntimeInstanceDispatchProvider,
            IAiRuntimeInstanceStatusProvider,
            IAiRuntimeInstanceControlProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                ArgumentNullException.ThrowIfNull(descriptor);

                if (descriptor.Metadata.TryGetValue(
                        AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                        out var providerName) &&
                    !string.IsNullOrWhiteSpace(providerName))
                {
                    return string.Equals(
                        providerName,
                        "local",
                        StringComparison.OrdinalIgnoreCase);
                }

                return true;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Dispatch is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Status is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Status is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this resolver unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this resolver unit test.");
            }
        }

        /// <summary>
        /// Test provider implementing only dispatch capability.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class TestDispatchOnlyProvider :
            IAiRuntimeInstanceDispatchProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                return true;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Dispatch is not used by this resolver unit test.");
            }
        }
    }
}