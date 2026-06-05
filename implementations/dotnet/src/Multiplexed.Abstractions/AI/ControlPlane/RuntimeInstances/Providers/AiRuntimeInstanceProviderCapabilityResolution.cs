using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Represents the result of resolving a runtime instance provider capability.
    /// </summary>
    /// <typeparam name="TProvider">The provider capability type.</typeparam>
    public sealed class AiRuntimeInstanceProviderCapabilityResolution<TProvider>
        where TProvider : IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Gets a value indicating whether provider capability resolution succeeded.
        /// </summary>
        public bool Success { get; private init; }

        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the resolved runtime instance capacity descriptor.
        /// </summary>
        public AiRuntimeInstanceCapacityDescriptor? Descriptor { get; private init; }

        /// <summary>
        /// Gets the resolved provider capability.
        /// </summary>
        public TProvider? Provider { get; private init; }

        /// <summary>
        /// Gets the failure reason when resolution failed.
        /// </summary>
        public string? FailureReason { get; private init; }

        /// <summary>
        /// Creates a successful provider capability resolution.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="descriptor">The runtime instance capacity descriptor.</param>
        /// <param name="provider">The resolved provider capability.</param>
        /// <returns>The successful resolution.</returns>
        public static AiRuntimeInstanceProviderCapabilityResolution<TProvider> Succeeded(
            string runtimeInstanceId,
            AiRuntimeInstanceCapacityDescriptor descriptor,
            TProvider provider)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(provider);

            return new AiRuntimeInstanceProviderCapabilityResolution<TProvider>
            {
                Success = true,
                RuntimeInstanceId = runtimeInstanceId,
                Descriptor = descriptor,
                Provider = provider
            };
        }

        /// <summary>
        /// Creates a failed provider capability resolution.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <returns>The failed resolution.</returns>
        public static AiRuntimeInstanceProviderCapabilityResolution<TProvider> Failed(
            string runtimeInstanceId,
            string failureReason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

            return new AiRuntimeInstanceProviderCapabilityResolution<TProvider>
            {
                Success = false,
                RuntimeInstanceId = runtimeInstanceId,
                FailureReason = failureReason
            };
        }
    }
}