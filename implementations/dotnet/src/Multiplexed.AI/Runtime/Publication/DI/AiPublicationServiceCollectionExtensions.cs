using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Publication.DI
{
    /// <summary>Opt-in server services only. Existing hosts, policy/step scanners and RBAC grants are unchanged.</summary>
    public static class AiPublicationServiceCollectionExtensions
    {
        public static IServiceCollection AddAiImmutablePublications(this IServiceCollection services, AiPublicationOptions options)
        {
            ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(options);
            if (services.Any(s => s.ServiceType == typeof(AiPublicationOptions)))
                throw new InvalidOperationException("Immutable publication services are already configured.");
            if (services.Any(s => s.ServiceType == typeof(IAiDurableInvocationTargetResolver)))
                throw new InvalidOperationException("A durable target resolver is already registered; conflicting resolution cannot be hidden.");
            services.AddSingleton(options);
            services.TryAddScoped<AiPublicationIdentity>();
            services.TryAddScoped<AiImmutablePublicationStore>();
            services.TryAddScoped<AiPipelinePublicationService>();
            services.TryAddScoped<AiPublishedDagRunService>();
            services.TryAddScoped<AiPublishedChildDagBindingCoordinator>();
            services.AddScoped<IAiDurableInvocationTargetResolver, AiPublicationInvocationTargetResolver>();
            return services;
        }
    }
}
