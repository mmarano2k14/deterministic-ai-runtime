using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.ControlPlane.Discovery.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.Discovery
{
    /// <summary>
    /// Provides service collection extensions for control-plane discovery services.
    /// </summary>
    public static class AiControlPlaneDiscoveryServiceCollectionExtensions
    {
        /// <summary>
        /// Adds core control-plane discovery services used to resolve the active control-plane identifier.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiControlPlaneDiscoveryCore(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<IAiControlPlaneDiscoveryStore, RedisAiControlPlaneDiscoveryStore>();
            services.AddSingleton<IAiControlPlaneIdResolver, DefaultAiControlPlaneIdResolver>();

            return services;
        }

        /// <summary>
        /// Adds the control-plane discovery publisher hosted service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiControlPlaneDiscoveryPublisher(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddHostedService<AiControlPlaneDiscoveryHostedService>();

            return services;
        }

        /// <summary>
        /// Adds all control-plane discovery services, including the resolver and publisher hosted service.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiControlPlaneDiscovery(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiControlPlaneDiscoveryPublisher();

            return services;
        }
    }
}