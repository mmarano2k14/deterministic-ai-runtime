using System.Reflection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Default runtime instance provider router.
    /// </summary>
    public sealed class AiRuntimeInstanceProviderRouter : IAiRuntimeInstanceProviderRouter
    {
        private const string DefaultProviderName = "local";

        private readonly IReadOnlyDictionary<string, IAiRuntimeInstanceProvider> providers;

        public AiRuntimeInstanceProviderRouter(
            IEnumerable<IAiRuntimeInstanceProvider> providers)
        {
            ArgumentNullException.ThrowIfNull(providers);

            this.providers = providers
                .Select(provider => new
                {
                    Provider = provider,
                    Attribute = provider
                        .GetType()
                        .GetCustomAttribute<AiRuntimeInstanceProviderAttribute>()
                })
                .Where(item => item.Attribute is not null)
                .GroupBy(
                    item => item.Attribute!.ProviderName,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var items = group.ToArray();

                        if (items.Length > 1)
                        {
                            throw new InvalidOperationException(
                                $"Multiple AI runtime instance providers are registered with provider name '{group.Key}'.");
                        }

                        return items[0].Provider;
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> ProviderNames =>
            providers.Keys.ToArray();

        public TProvider GetRequiredProvider<TProvider>(
            AiRuntimeInstanceCapacityDescriptor descriptor)
            where TProvider : IAiRuntimeInstanceProvider
        {
            if (TryGetProvider<TProvider>(
                    descriptor,
                    out var provider))
            {
                return provider;
            }

            var providerName =
                ResolveProviderName(descriptor);

            throw new InvalidOperationException(
                $"No AI runtime instance provider capability '{typeof(TProvider).FullName}' is registered for provider name '{providerName}'.");
        }

        public bool TryGetProvider<TProvider>(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            out TProvider provider)
            where TProvider : IAiRuntimeInstanceProvider
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            provider = default!;

            var providerName =
                ResolveProviderName(descriptor);

            if (!providers.TryGetValue(
                    providerName,
                    out var resolvedProvider))
            {
                return false;
            }

            if (resolvedProvider is not TProvider typedProvider)
            {
                return false;
            }

            if (!typedProvider.CanHandle(descriptor))
            {
                return false;
            }

            provider = typedProvider;

            return true;
        }

        private static string ResolveProviderName(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.Metadata is not null &&
                descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                return providerName.Trim();
            }

            return DefaultProviderName;
        }
    }
}