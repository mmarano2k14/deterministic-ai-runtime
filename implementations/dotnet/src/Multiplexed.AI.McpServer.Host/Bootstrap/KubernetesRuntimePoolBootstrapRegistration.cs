using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Activates the in-Pod Runtime Pool composition after the standard RuntimeInstanceOnly
    /// services have been registered.
    /// </summary>
    public static class KubernetesRuntimePoolBootstrapRegistration
    {
        private static readonly string[] SingleRuntimeHostedServiceNames =
        {
            "AiRuntimePipelineBackgroundControllerHostedService",
            "AiRuntimeInstanceRegistrationHostedService"
        };

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

            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(options);

            var descriptorsToRemove =
                services
                    .Where(
                        descriptor =>
                            descriptor.ServiceType
                                == typeof(IHostedService)
                            && descriptor.ImplementationType is not null
                            && SingleRuntimeHostedServiceNames.Contains(
                                descriptor.ImplementationType.Name,
                                StringComparer.Ordinal))
                    .ToArray();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }

            services.AddAiKubernetesRuntimePoolInPod(options);
        }
    }
}
