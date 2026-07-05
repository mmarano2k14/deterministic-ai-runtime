using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Local;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Readiness;
using System;
using System.Linq;
using System.Reflection;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
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

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeHostCreationStrategy,
                    FixtureAiRuntimeHostCreationStrategy>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiRuntimeHostCreationStrategy, FixtureAiRuntimeHostCreationStrategy>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAiRuntimeHostCreationStrategy, ProcessAiRuntimeHostCreationStrategy>());

            services.TryAddSingleton<IAiRuntimeHostProcessControl>(
                provider => provider
                    .GetRequiredService<IEnumerable<IAiRuntimeHostCreationStrategy>>()
                    .OfType<ProcessAiRuntimeHostCreationStrategy>()
                    .Single());

            services.TryAddSingleton<
                IAiRuntimeHostManager,
                AiRuntimeHostCreationManager>();

            services.TryAddSingleton<
                IAiRuntimeInstanceReadinessWaiter,
                AiRuntimeInstanceReadinessWaiter>();

            return services;
        }

        /// <summary>
        /// Registers the built-in runtime instance providers.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration used to bind runtime instance provider options.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceProviders(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.Configure<AiRuntimeProcessHostCreationOptions>(
                configuration.GetSection("AiRuntimeProcessHostCreation"));

            return services.AddAiRuntimeInstanceProviders();
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

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeHostCreationStrategy,
                    FixtureAiRuntimeHostCreationStrategy>());

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IAiRuntimeHostCreationStrategy,
                    ProcessAiRuntimeHostCreationStrategy>());

            services.TryAddSingleton<
                IAiRuntimeHostManager,
                AiRuntimeHostCreationManager>();

            services.TryAddSingleton<
                IAiRuntimeInstanceReadinessWaiter,
                AiRuntimeInstanceReadinessWaiter>();

            return services;
        }

        /// <summary>
        /// Registers runtime instance providers discovered from the supplied assemblies.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration used to bind runtime instance provider options.</param>
        /// <param name="assemblies">The assemblies to scan for runtime instance providers.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiRuntimeInstanceProvidersFromAssemblies(
            this IServiceCollection services,
            IConfiguration configuration,
            params Assembly[] assemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(assemblies);

            services.Configure<AiRuntimeProcessHostCreationOptions>(
                configuration.GetSection("AiRuntimeProcessHostCreation"));

            return services.AddAiRuntimeInstanceProvidersFromAssemblies(assemblies);
        }
    }
}