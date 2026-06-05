using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Provides dependency injection registration for runtime instance providers.
    /// </summary>
    public static class AiRuntimeInstanceProviderServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the built-in runtime instance providers.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceProviders(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeInstanceProvider,
                    LocalAiRuntimeInstanceProvider>());

            services.TryAddSingleton<
                IAiRuntimeInstanceProviderRouter,
                AiRuntimeInstanceProviderRouter>();

            services.TryAddSingleton<
                IAiRuntimeInstanceProviderCapabilityResolver,
                AiRuntimeInstanceProviderCapabilityResolver>();

            return services;
        }

        /// <summary>
        /// Registers runtime instance providers discovered from the supplied assemblies.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="assemblies">The assemblies to scan for runtime instance providers.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceProvidersFromAssemblies(
            this IServiceCollection services,
            params Assembly[] assemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(assemblies);

            foreach (var assembly in assemblies.Distinct())
            {
                var providerTypes = assembly
                    .GetTypes()
                    .Where(type =>
                        !type.IsAbstract &&
                        !type.IsInterface &&
                        typeof(IAiRuntimeInstanceProvider).IsAssignableFrom(type) &&
                        type.GetCustomAttribute<AiRuntimeInstanceProviderAttribute>() is not null);

                foreach (var providerType in providerTypes)
                {
                    services.TryAddEnumerable(
                        ServiceDescriptor.Singleton(
                            typeof(IAiRuntimeInstanceProvider),
                            providerType));
                }
            }

            services.TryAddSingleton<
                IAiRuntimeInstanceProviderRouter,
                AiRuntimeInstanceProviderRouter>();

            services.TryAddSingleton<
                IAiRuntimeInstanceProviderCapabilityResolver,
                AiRuntimeInstanceProviderCapabilityResolver>();

            return services;
        }
    }
}