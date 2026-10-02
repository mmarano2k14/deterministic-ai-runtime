using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DI
{
    /// <summary>Explicit opt-in for hosted custom Delegation policies.</summary>
    public static class AiHostedDelegationPolicyServiceCollectionExtensions
    {
        public static IServiceCollection AddAiHostedDelegationPolicyExecution(
            this IServiceCollection services,
            AiDelegationPolicyInvocationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            if (!services.Any(service => service.ServiceType == typeof(IAiWorkerInvocationTransport)))
            {
                throw new InvalidOperationException(
                    "Configure hosted invocation workers before hosted custom Delegation policies.");
            }

            if (!services.Any(service => service.ServiceType == typeof(AiPublicationOptions)) ||
                !services.Any(service => service.ServiceType == typeof(AiPublicationIdentity)) ||
                !services.Any(service => service.ServiceType == typeof(AiImmutablePublicationStore)))
            {
                throw new InvalidOperationException(
                    "Configure immutable publications before hosted custom Delegation policies.");
            }

            if (services.Any(service => service.ServiceType == typeof(AiDelegationPolicyAdapterFactory)) ||
                services.Any(service => service.ServiceType == typeof(IAiDelegationPolicyTransport)))
            {
                throw new InvalidOperationException(
                    "Custom Delegation policy execution is already configured.");
            }

            if (services.Any(service => service.ServiceType == typeof(AiDelegationPolicyInvocationOptions)))
            {
                if (options is not null)
                {
                    throw new InvalidOperationException(
                        "Custom Delegation policy options are already configured.");
                }
            }
            else
            {
                services.AddSingleton(options ?? new AiDelegationPolicyInvocationOptions());
            }

            services.TryAddScoped<IAiDelegationPolicyCodePreparer, AiDelegationPolicyPublicationPreparer>();
            services.AddScoped<IAiDelegationPolicyTransport>(provider =>
                new AiHostedDelegationPolicyTransport(
                    AiExecutionLanguages.Python,
                    provider.GetRequiredService<IAiDelegationPolicyCodePreparer>(),
                    provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                    provider.GetService<TimeProvider>()));
            services.AddScoped<IAiDelegationPolicyTransport>(provider =>
                new AiHostedDelegationPolicyTransport(
                    AiExecutionLanguages.TypeScript,
                    provider.GetRequiredService<IAiDelegationPolicyCodePreparer>(),
                    provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                    provider.GetService<TimeProvider>()));
            services.AddScoped<IAiDelegationPolicyTransport>(provider =>
                new AiHostedDelegationPolicyTransport(
                    AiExecutionLanguages.DotNet,
                    provider.GetRequiredService<IAiDelegationPolicyCodePreparer>(),
                    provider.GetRequiredService<IAiWorkerInvocationTransport>(),
                    provider.GetService<TimeProvider>()));
            services.AddScoped<AiDelegationPolicyAdapterFactory>();
            return services;
        }
    }
}
