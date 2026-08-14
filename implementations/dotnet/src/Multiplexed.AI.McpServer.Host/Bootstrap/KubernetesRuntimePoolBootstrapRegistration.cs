using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Activates the in-Pod Runtime Pool composition after the standard RuntimeInstanceOnly
    /// services have been registered.
    /// </summary>
    public static class KubernetesRuntimePoolBootstrapRegistration
    {
        /// <summary>
        /// Configures the in-Pod Runtime Pool when explicitly enabled.
        /// </summary>
        public static void Configure(
            IServiceCollection services,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            var options =
                configuration
                    .GetSection("AiKubernetesRuntimePoolInPod")
                    .Get<AiKubernetesRuntimePoolInPodOptions>()
                ?? new AiKubernetesRuntimePoolInPodOptions();

            if (!options.Enabled)
            {
                return;
            }

            var processPoolOptions =
                configuration
                    .GetSection("AiRuntimeProcessPool")
                    .Get<AiRuntimeProcessPoolOptions>();

            if (processPoolOptions?.Enabled == true)
            {
                throw new InvalidOperationException(
                    "AiKubernetesRuntimePoolInPod and AiRuntimeProcessPool cannot both be enabled in the same RuntimeInstanceOnly host.");
            }

            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options);

            RuntimePoolBootstrapHostedServiceRegistration
                .RemoveSingleRuntimeHostedServices(services);

            services.AddAiKubernetesRuntimePoolInPod(options);
        }
    }
}
