using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing
{
    /// <summary>
    /// Test runtime instance providers used by provider unit tests.
    /// </summary>
    internal static class TestRuntimeInstanceProviders
    {
        /// <summary>
        /// Creates a full local provider implementing dispatch, status, and control capabilities.
        /// </summary>
        /// <returns>The provider.</returns>
        public static IAiRuntimeInstanceProvider CreateFullLocalProvider()
        {
            return new FullLocalProvider();
        }

        /// <summary>
        /// Creates a dispatch-only local provider.
        /// </summary>
        /// <returns>The provider.</returns>
        public static IAiRuntimeInstanceProvider CreateDispatchOnlyLocalProvider()
        {
            return new DispatchOnlyLocalProvider();
        }

        /// <summary>
        /// Test provider implementing dispatch, status, and control capabilities.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class FullLocalProvider :
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
                    "Dispatch is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Status is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Status is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this provider unit test.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException(
                    "Control is not used by this provider unit test.");
            }
        }

        /// <summary>
        /// Test provider implementing only dispatch capability.
        /// </summary>
        [AiRuntimeInstanceProvider("local")]
        private sealed class DispatchOnlyLocalProvider :
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
                    "Dispatch is not used by this provider unit test.");
            }
        }
    }
}