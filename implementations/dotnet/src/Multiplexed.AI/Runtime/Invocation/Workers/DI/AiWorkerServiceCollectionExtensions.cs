using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Durable.DI;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DI
{
    /// <summary>Explicit host configuration only; existing runtime modes and native scanners remain untouched.</summary>
    public static class AiWorkerServiceCollectionExtensions
    {
        public static IServiceCollection AddAiHostedInvocationWorkers(this IServiceCollection services,
            IAiWorkerProcessCatalog catalog, AiWorkerSupervisionOptions supervision,
            AiWorkerProcessTransportOptions? transport = null,
            AiWorkerExecutionAdmissionPolicy? executionPolicy = null)
        {
            ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(supervision);
            if (services.Any(s => s.ServiceType == typeof(AiWorkerSupervisionOptions)))
                throw new InvalidOperationException("Hosted invocation workers are already configured.");
            if (executionPolicy is not null && services.Any(s => s.ServiceType == typeof(AiWorkerExecutionAdmissionPolicy)))
                throw new InvalidOperationException("Worker execution admission policy is already configured.");
            services.TryAddSingleton(executionPolicy ?? AiWorkerExecutionAdmissionPolicy.LegacyCompatible);
            services.AddAiDurableInvocationJournal();
            services.TryAddSingleton<TimeProvider>(TimeProvider.System);
            services.AddSingleton(catalog); services.AddSingleton(supervision);
            services.AddSingleton(transport ?? new AiWorkerProcessTransportOptions());
            services.TryAddSingleton<AiWorkerProcessCapacity>();
            services.TryAddSingleton<IAiWorkerInvocationTransport, AiWorkerProcessTransport>();
            services.TryAddScoped<AiImmutableJsonPayloadReader>();
            services.TryAddScoped<AiDurableInvocationDagBinding>();
            services.TryAddScoped<IAiWorkerInvocationPreparer, AiWorkerPublicationPreparer>();
            services.TryAddScoped<AiWorkerInvocationSupervisor>();
            services.TryAddScoped<AiWorkerDispatchPageReader>();
            return services;
        }
        public static IServiceCollection AddAiHostedInvocationWorkerPolling(this IServiceCollection services, AiWorkerPollingOptions options)
        {
            ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options);
            if (!services.Any(s => s.ServiceType == typeof(AiWorkerSupervisionOptions)))
                throw new InvalidOperationException("Configure hosted invocation workers before enabling polling.");
            if (services.Any(s => s.ServiceType == typeof(AiWorkerPollingOptions)))
                throw new InvalidOperationException("Worker polling is already configured.");
            services.AddSingleton(options);
            services.AddHostedService<AiWorkerDispatchHostedService>();
            return services;
        }
    }
}
