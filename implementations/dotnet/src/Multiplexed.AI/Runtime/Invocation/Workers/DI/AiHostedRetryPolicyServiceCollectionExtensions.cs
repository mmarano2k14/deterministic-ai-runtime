using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Workers.Policies;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.DI
{
    /// <summary>Explicit opt-in for hosted custom Retry policies.</summary>
    public static class AiHostedRetryPolicyServiceCollectionExtensions
    {
        public static IServiceCollection AddAiHostedRetryPolicyExecution(this IServiceCollection services,AiRetryPolicyInvocationOptions? options=null)
        {
            ArgumentNullException.ThrowIfNull(services);
            if(!services.Any(s=>s.ServiceType==typeof(IAiWorkerInvocationTransport))) throw new InvalidOperationException("Configure hosted invocation workers before hosted custom Retry policies.");
            if(!services.Any(s=>s.ServiceType==typeof(AiPublicationOptions))||!services.Any(s=>s.ServiceType==typeof(AiPublicationIdentity))||!services.Any(s=>s.ServiceType==typeof(AiImmutablePublicationStore))) throw new InvalidOperationException("Configure immutable publications before hosted custom Retry policies.");
            if(services.Any(s=>s.ServiceType==typeof(AiRetryPolicyAdapterFactory))||services.Any(s=>s.ServiceType==typeof(IAiRetryPolicyTransport))) throw new InvalidOperationException("Custom Retry policy execution is already configured.");
            if(services.Any(s=>s.ServiceType==typeof(AiRetryPolicyInvocationOptions))){if(options is not null)throw new InvalidOperationException("Custom Retry policy options are already configured.");}
            else services.AddSingleton(options??new AiRetryPolicyInvocationOptions());
            services.TryAddScoped<IAiRetryPolicyCodePreparer,AiRetryPolicyPublicationPreparer>();
            services.AddScoped<IAiRetryPolicyTransport>(p=>new AiHostedRetryPolicyTransport(AiExecutionLanguages.Python,p.GetRequiredService<IAiRetryPolicyCodePreparer>(),p.GetRequiredService<IAiWorkerInvocationTransport>(),p.GetService<TimeProvider>()));
            services.AddScoped<IAiRetryPolicyTransport>(p=>new AiHostedRetryPolicyTransport(AiExecutionLanguages.TypeScript,p.GetRequiredService<IAiRetryPolicyCodePreparer>(),p.GetRequiredService<IAiWorkerInvocationTransport>(),p.GetService<TimeProvider>()));
            services.AddScoped<IAiRetryPolicyTransport>(p=>new AiHostedRetryPolicyTransport(AiExecutionLanguages.DotNet,p.GetRequiredService<IAiRetryPolicyCodePreparer>(),p.GetRequiredService<IAiWorkerInvocationTransport>(),p.GetService<TimeProvider>()));
            services.AddScoped<AiRetryPolicyAdapterFactory>(); return services;
        }
    }
}
