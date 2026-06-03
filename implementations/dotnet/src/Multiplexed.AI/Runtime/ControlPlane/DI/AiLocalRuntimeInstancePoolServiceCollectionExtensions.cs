using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.AI.ControlPlane.RuntimeInstances.Pool;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Provides dependency injection registration for the local runtime instance pool.
    /// </summary>
    public static class AiLocalRuntimeInstancePoolServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the local runtime instance pool services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Optional local runtime instance pool options configuration.</param>
        /// <returns>The same service collection for chaining.</returns>
        public static IServiceCollection AddAiLocalRuntimeInstancePool(
            this IServiceCollection services,
            Action<AiLocalRuntimeInstancePoolOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is null)
            {
                services.AddOptions<AiLocalRuntimeInstancePoolOptions>();
            }
            else
            {
                services.Configure(configure);
            }

            services.TryAddSingleton<IAiLocalRuntimeInstanceServiceCollectionProvider>(
                new AiLocalRuntimeInstanceServiceCollectionProvider(services));

            services.TryAddSingleton<
                IAiLocalRuntimeInstanceHostFactory,
                AiLocalRuntimeInstanceHostFactory>();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<
                    IHostedService,
                    AiLocalRuntimeInstancePoolHostedService>());

            return services;
        }
    }
}