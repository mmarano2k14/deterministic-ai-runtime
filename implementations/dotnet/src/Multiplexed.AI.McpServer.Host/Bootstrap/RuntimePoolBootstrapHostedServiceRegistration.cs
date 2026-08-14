using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Removes single-runtime hosted services before either Runtime Pool composition is activated.
    /// </summary>
    internal static class RuntimePoolBootstrapHostedServiceRegistration
    {
        private static readonly string[] SingleRuntimeHostedServiceNames =
        {
            "AiRuntimePipelineBackgroundControllerHostedService",
            "AiRuntimeInstanceRegistrationHostedService"
        };

        /// <summary>
        /// Removes the parent single-runtime lifecycle so only pool children become dispatchable.
        /// </summary>
        public static void RemoveSingleRuntimeHostedServices(
            IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            var descriptorsToRemove =
                services
                    .Where(
                        descriptor =>
                            descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType is not null
                            && SingleRuntimeHostedServiceNames.Contains(
                                descriptor.ImplementationType.Name,
                                StringComparer.Ordinal))
                    .ToArray();

            foreach (var descriptor in descriptorsToRemove)
            {
                services.Remove(descriptor);
            }
        }
    }
}
