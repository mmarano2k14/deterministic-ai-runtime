using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.AI.Runtime.ControlPlane.Signals;

namespace Multiplexed.AI.Runtime.ControlPlane.DI
{
    /// <summary>
    /// Registers internal runtime signal services.
    /// </summary>
    public static class AiRuntimeSignalServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the Redis-backed runtime signal publisher and subscriber.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddAiRuntimeSignals(
            this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.TryAddSingleton<
                IAiRuntimeSignalPublisher,
                RedisAiRuntimeSignalPublisher>();

            services.TryAddSingleton<
                IAiRuntimeSignalSubscriber,
                RedisAiRuntimeSignalSubscriber>();

            return services;
        }
    }
}