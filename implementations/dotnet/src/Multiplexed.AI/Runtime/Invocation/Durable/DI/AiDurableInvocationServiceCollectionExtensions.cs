using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;

namespace Multiplexed.AI.Runtime.Invocation.Durable.DI
{
    /// <summary>Explicit server opt-in. No native step/policy, worker or hosted service is registered.</summary>
    public static class AiDurableInvocationServiceCollectionExtensions
    {
        /// <summary>
        /// Reuses the host's IMongoDatabase and optional TimeProvider. Does not create a
        /// MongoClient, change RBAC, enable an executable custom capability or start I/O.
        /// </summary>
        public static IServiceCollection AddAiDurableInvocationJournal(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddOptions<AiDurableInvocationMongoOptions>();
            services.TryAddSingleton<IAiDurableInvocationStore, MongoAiDurableInvocationStore>();
            services.TryAddSingleton<AiDurableInvocationJournal>(provider => new AiDurableInvocationJournal(
                provider.GetRequiredService<IAiDurableInvocationStore>(), provider.GetService<TimeProvider>()));
            return services;
        }
    }
}
