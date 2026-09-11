using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;

namespace Multiplexed.AI.Runtime.Invocation.Durable.DI
{
    /// <summary>Registers server integration only. Existing hosts and the native scanners remain unchanged.</summary>
    public static class AiDurableInvocationDagServiceCollectionExtensions
    {
        public static IServiceCollection AddAiDurableInvocationDag(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddAiDurableInvocationJournal();
            services.TryAddScoped<AiImmutableJsonPayloadReader>();
            services.TryAddScoped<AiDurableInvocationDagBinding>();
            services.TryAddScoped<AiDurableInvocationDagContinuationScheduler>();
            services.TryAddScoped<AiDurableInvocationDagContinuationCoordinator>();
            services.TryAddScoped<AiDurableInvocationDagReconciler>();
            foreach (var language in new[] { "python", "typescript", "dotnet" })
                if (!services.Any(item => item.ServiceType == typeof(IAiStepInvocationAdapterFactory) &&
                    item.ImplementationInstance is AiDurableInvocationStepAdapterFactory factory && factory.ExecutionLanguage == language))
                    services.AddSingleton<IAiStepInvocationAdapterFactory>(new AiDurableInvocationStepAdapterFactory(language));
            return services;
        }

        public static IServiceCollection AddAiDurableInvocationDagReconciliation(this IServiceCollection services,
            AiDurableInvocationDagReconciliationOptions options)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);
            if (services.Any(item => item.ServiceType == typeof(AiDurableInvocationDagReconciliationOptions)))
                throw new InvalidOperationException("Durable invocation reconciliation is already configured.");
            services.AddAiDurableInvocationDag();
            services.AddSingleton(options);
            services.AddHostedService<AiDurableInvocationDagReconcilerHostedService>();
            return services;
        }
    }
}
