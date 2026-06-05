using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Tests.Unit.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Unit tests for <see cref="AiRuntimeInstanceProviderRouter"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceProviderRouterTests
    {
        /// <summary>
        /// Verifies that the router resolves the local provider when the descriptor
        /// explicitly declares <c>provider.name = local</c>.
        /// </summary>
        [Fact]
        public void TryGetProvider_WithLocalProviderMetadata_ShouldResolveLocalProvider()
        {
            var provider = new TestLocalDispatchProvider();

            var router = new AiRuntimeInstanceProviderRouter(
                new IAiRuntimeInstanceProvider[]
                {
                    provider
                });

            var descriptor = CreateDescriptor(
                new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                });

            var resolved = router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                descriptor,
                out var resolvedProvider);

            Assert.True(resolved);
            Assert.Same(provider, resolvedProvider);
        }

        /// <summary>
        /// Verifies that the router falls back to the local provider when no provider
        /// metadata is present.
        /// </summary>
        [Fact]
        public void TryGetProvider_WithoutProviderMetadata_ShouldResolveLocalProviderByDefault()
        {
            var provider = new TestLocalDispatchProvider();

            var router = new AiRuntimeInstanceProviderRouter(
                new IAiRuntimeInstanceProvider[]
                {
                    provider
                });

            var descriptor = CreateDescriptor();

            var resolved = router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                descriptor,
                out var resolvedProvider);

            Assert.True(resolved);
            Assert.Same(provider, resolvedProvider);
        }

        /// <summary>
        /// Verifies that the router returns false when the descriptor references an
        /// unknown provider name.
        /// </summary>
        [Fact]
        public void TryGetProvider_WithUnknownProviderMetadata_ShouldReturnFalse()
        {
            var router = new AiRuntimeInstanceProviderRouter(
                new IAiRuntimeInstanceProvider[]
                {
                    new TestLocalDispatchProvider()
                });

            var descriptor = CreateDescriptor(
                new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "unknown"
                });

            var resolved = router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                descriptor,
                out var resolvedProvider);

            Assert.False(resolved);
            Assert.Null(resolvedProvider);
        }

        /// <summary>
        /// Verifies that <see cref="IAiRuntimeInstanceProviderRouter.GetRequiredProvider{TProvider}"/>
        /// throws when no provider can be resolved for the descriptor.
        /// </summary>
        [Fact]
        public void GetRequiredProvider_WithUnknownProviderMetadata_ShouldThrow()
        {
            var router = new AiRuntimeInstanceProviderRouter(
                new IAiRuntimeInstanceProvider[]
                {
                    new TestLocalDispatchProvider()
                });

            var descriptor = CreateDescriptor(
                new Dictionary<string, string>
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "unknown"
                });

            var exception = Assert.Throws<InvalidOperationException>(
                () => router.GetRequiredProvider<IAiRuntimeInstanceDispatchProvider>(
                    descriptor));

            Assert.Contains(
                "unknown",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that duplicate provider names fail fast during router construction.
        /// </summary>
        [Fact]
        public void Constructor_WithDuplicateProviderNames_ShouldThrow()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => new AiRuntimeInstanceProviderRouter(
                    new IAiRuntimeInstanceProvider[]
                    {
                        new TestLocalDispatchProvider(),
                        new DuplicateLocalDispatchProvider()
                    }));

            Assert.Contains(
                "local",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verifies that providers without <see cref="AiRuntimeInstanceProviderAttribute"/>
        /// are ignored by the router.
        /// </summary>
        [Fact]
        public void TryGetProvider_WithProviderWithoutAttribute_ShouldReturnFalse()
        {
            var router = new AiRuntimeInstanceProviderRouter(
                new IAiRuntimeInstanceProvider[]
                {
                    new ProviderWithoutAttribute()
                });

            var descriptor = CreateDescriptor();

            var resolved = router.TryGetProvider<IAiRuntimeInstanceDispatchProvider>(
                descriptor,
                out var resolvedProvider);

            Assert.False(resolved);
            Assert.Null(resolvedProvider);
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor for router tests.
        /// </summary>
        /// <param name="metadata">The optional descriptor metadata.</param>
        /// <returns>The runtime instance capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "mcp-runtime-1",
                Metadata =
                    metadata ??
                    new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Test local dispatch provider.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class TestLocalDispatchProvider : IAiRuntimeInstanceDispatchProvider
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
                    "Dispatch is not used by this router unit test.");
            }
        }

        /// <summary>
        /// Duplicate local dispatch provider used to validate duplicate provider-name detection.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class DuplicateLocalDispatchProvider : IAiRuntimeInstanceDispatchProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return true;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Dispatch is not used by this router unit test.");
            }
        }

        /// <summary>
        /// Provider without attribute used to validate that only attributed providers are registered.
        /// </summary>
        private sealed class ProviderWithoutAttribute : IAiRuntimeInstanceDispatchProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return true;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Dispatch is not used by this router unit test.");
            }
        }
    }
}