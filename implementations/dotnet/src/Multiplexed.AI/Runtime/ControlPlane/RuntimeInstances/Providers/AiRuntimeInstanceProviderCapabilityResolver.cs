using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Default runtime instance provider capability resolver.
    /// </summary>
    public sealed class AiRuntimeInstanceProviderCapabilityResolver :
        IAiRuntimeInstanceProviderCapabilityResolver
    {
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeInstanceProviderRouter providerRouter;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceProviderCapabilityResolver"/> class.
        /// </summary>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="providerRouter">The runtime instance provider router.</param>
        public AiRuntimeInstanceProviderCapabilityResolver(
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeInstanceProviderRouter providerRouter)
        {
            this.capacityStore =
                capacityStore
                ?? throw new ArgumentNullException(nameof(capacityStore));

            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
            where TProvider : IAiRuntimeInstanceProvider
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var descriptor =
                await capacityStore
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (descriptor is null)
            {
                return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                    runtimeInstanceId,
                    $"Runtime instance capacity descriptor '{runtimeInstanceId}' was not found.");
            }

            if (!providerRouter.TryGetProvider<TProvider>(
                    descriptor,
                    out var provider))
            {
                return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                    runtimeInstanceId,
                    $"No provider capability '{typeof(TProvider).FullName}' was found for runtime instance '{runtimeInstanceId}'.");
            }

            return AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Succeeded(
                runtimeInstanceId,
                descriptor,
                provider);
        }
    }
}