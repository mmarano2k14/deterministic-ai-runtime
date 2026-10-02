using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DI
{
    /// <summary>
    /// Adds the isolated OCI provider behind the existing hosted invocation boundary.
    /// The durable supervisor, process-wide capacity and hosted policy transports continue to depend
    /// on one IAiWorkerInvocationTransport and therefore do not acquire provider-specific authority.
    /// </summary>
    public static class AiContainerWorkerServiceCollectionExtensions
    {
        public static IServiceCollection AddAiHostedInvocationContainerWorkers(
            this IServiceCollection services,
            IAiContainerWorkerCatalog catalog)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(catalog);

            if (!services.Any(service => service.ServiceType == typeof(AiWorkerSupervisionOptions)) ||
                !services.Any(service => service.ServiceType == typeof(IAiWorkerProcessCatalog)))
            {
                throw new InvalidOperationException(
                    "Configure hosted invocation workers before adding the isolated container provider.");
            }

            if (services.Any(service => service.ServiceType == typeof(IAiContainerWorkerCatalog)) ||
                services.Any(service => service.ServiceType == typeof(AiWorkerInvocationTransportRouter)))
            {
                throw new InvalidOperationException("Hosted isolated container workers are already configured.");
            }

            var currentTransports = services
                .Where(service => service.ServiceType == typeof(IAiWorkerInvocationTransport))
                .ToArray();
            if (currentTransports.Length != 1 ||
                currentTransports[0].ImplementationType != typeof(AiWorkerProcessTransport))
            {
                throw new InvalidOperationException(
                    "The isolated provider can only compose with the default trusted-process hosted transport.");
            }

            services.RemoveAll<IAiWorkerInvocationTransport>();
            services.TryAddSingleton<AiWorkerProcessTransport>();
            services.AddSingleton(catalog);
            services.TryAddSingleton<AiContainerWorkerTransport>(provider => new AiContainerWorkerTransport(
                provider.GetRequiredService<IAiContainerWorkerCatalog>(),
                provider.GetRequiredService<AiWorkerProcessTransportOptions>(),
                provider.GetRequiredService<TimeProvider>(),
                new AiWorkerExecutionAdmissionPolicy()));
            services.TryAddSingleton<AiWorkerInvocationTransportRouter>();
            services.AddSingleton<IAiWorkerInvocationTransport>(provider =>
                provider.GetRequiredService<AiWorkerInvocationTransportRouter>());
            return services;
        }
    }
}
