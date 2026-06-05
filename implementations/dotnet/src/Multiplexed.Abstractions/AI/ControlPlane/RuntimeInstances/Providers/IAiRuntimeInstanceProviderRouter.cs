using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Resolves runtime instance providers by descriptor and capability.
    /// </summary>
    public interface IAiRuntimeInstanceProviderRouter
    {
        /// <summary>
        /// Gets the registered provider names.
        /// </summary>
        IReadOnlyCollection<string> ProviderNames { get; }

        /// <summary>
        /// Resolves a required provider capability for the specified descriptor.
        /// </summary>
        TProvider GetRequiredProvider<TProvider>(
            AiRuntimeInstanceCapacityDescriptor descriptor)
            where TProvider : IAiRuntimeInstanceProvider;

        /// <summary>
        /// Attempts to resolve a provider capability for the specified descriptor.
        /// </summary>
        bool TryGetProvider<TProvider>(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            out TProvider provider)
            where TProvider : IAiRuntimeInstanceProvider;
    }
}