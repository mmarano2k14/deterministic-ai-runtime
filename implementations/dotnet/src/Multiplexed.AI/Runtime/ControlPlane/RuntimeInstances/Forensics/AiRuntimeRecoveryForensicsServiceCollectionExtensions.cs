using System;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides service registration extensions for runtime recovery forensics.
    /// </summary>
    public static class AiRuntimeRecoveryForensicsServiceCollectionExtensions
    {
        /// <summary>
        /// Adds no-op runtime recovery forensics services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddNoopAiRuntimeRecoveryForensics(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<IAiRuntimeRecoveryForensicsRecorder, NoopAiRuntimeRecoveryForensicsRecorder>();

            return services;
        }

        /// <summary>
        /// Adds in-memory runtime recovery forensics services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The optional options configuration delegate.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddInMemoryAiRuntimeRecoveryForensics(
            this IServiceCollection services,
            Action<AiRuntimeRecoveryForensicsOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (configure is not null)
            {
                services.Configure(configure);
            }
            else
            {
                services.Configure<AiRuntimeRecoveryForensicsOptions>(_ => { });
            }

            services.AddSingleton<IAiRuntimeRecoveryForensicsStore, InMemoryAiRuntimeRecoveryForensicsStore>();
            services.AddSingleton<IAiRuntimeRecoveryForensicsRecorder, BestEffortAiRuntimeRecoveryForensicsRecorder>();

            return services;
        }
    }
}