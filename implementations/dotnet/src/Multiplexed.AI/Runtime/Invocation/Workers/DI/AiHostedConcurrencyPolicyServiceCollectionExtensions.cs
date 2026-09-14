using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DI
{
    /// <summary>
    /// Explicit opt-in for hosted custom concurrency policies. It reuses immutable
    /// publications, the configured worker process transport and the existing RBAC path.
    /// Native policy discovery and DAG orchestration are not modified.
    /// </summary>
    public static class AiHostedConcurrencyPolicyServiceCollectionExtensions
    {
        public static IServiceCollection AddAiHostedConcurrencyPolicyExecution(
            this IServiceCollection services,
            AiConcurrencyPolicyInvocationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            if (!services.Any(s => s.ServiceType == typeof(IAiWorkerInvocationTransport)))
            {
                throw new InvalidOperationException(
                    "Configure hosted invocation workers before hosted custom policies.");
            }
            if (!services.Any(s => s.ServiceType == typeof(AiPublicationOptions)) ||
                !services.Any(s => s.ServiceType == typeof(AiPublicationIdentity)) ||
                !services.Any(s => s.ServiceType == typeof(AiImmutablePublicationStore)))
            {
                throw new InvalidOperationException(
                    "Configure immutable publications before hosted custom policies.");
            }
            if (services.Any(s => s.ServiceType == typeof(AiConcurrencyPolicyAdapterFactory)) ||
                services.Any(s => s.ServiceType == typeof(IAiConcurrencyPolicyTransport)))
            {
                throw new InvalidOperationException("Custom concurrency policy execution is already configured.");
            }

            if (services.Any(s => s.ServiceType == typeof(AiConcurrencyPolicyInvocationOptions)))
            {
                if (options is not null)
                {
                    throw new InvalidOperationException("Custom concurrency policy options are already configured.");
                }
            }
            else
            {
                services.AddSingleton(options ?? new AiConcurrencyPolicyInvocationOptions());
            }

            services.TryAddScoped<IAiConcurrencyPolicyCodePreparer, AiConcurrencyPolicyPublicationPreparer>();
            services.AddScoped<IAiConcurrencyPolicyTransport>(provider => new AiHostedConcurrencyPolicyTransport(
                AiExecutionLanguages.Python,
                provider.GetRequiredService<IAiConcurrencyPolicyCodePreparer>(),
                provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                provider.GetService<TimeProvider>()));
            services.AddScoped<IAiConcurrencyPolicyTransport>(provider => new AiHostedConcurrencyPolicyTransport(
                AiExecutionLanguages.TypeScript,
                provider.GetRequiredService<IAiConcurrencyPolicyCodePreparer>(),
                provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                provider.GetService<TimeProvider>()));
            services.AddScoped<IAiConcurrencyPolicyTransport>(provider => new AiHostedConcurrencyPolicyTransport(
                AiExecutionLanguages.DotNet,
                provider.GetRequiredService<IAiConcurrencyPolicyCodePreparer>(),
                provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                provider.GetService<TimeProvider>()));
            services.AddScoped<AiConcurrencyPolicyAdapterFactory>();
            return services;
        }
    }
}
