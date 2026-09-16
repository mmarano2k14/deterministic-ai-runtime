using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Invocation.Mcp.Durable;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo;

namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable.DI
{
    /// <summary>Explicit server opt-in for durable outbound MCP effect evidence.</summary>
    public static class AiMcpEffectEvidenceServiceCollectionExtensions
    {
        /// <summary>
        /// Reuses the host IMongoDatabase and optional TimeProvider. This registration does
        /// not alter outbound MCP execution until a later integration explicitly consumes it.
        /// </summary>
        public static IServiceCollection AddAiDurableMcpEffectEvidence(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddOptions<AiMcpEffectEvidenceMongoOptions>();
            services.TryAddSingleton<IAiMcpEffectEvidenceStore, MongoAiMcpEffectEvidenceStore>();
            services.TryAddSingleton<AiMcpEffectEvidenceJournal>(provider => new AiMcpEffectEvidenceJournal(
                provider.GetRequiredService<IAiMcpEffectEvidenceStore>(),
                provider.GetService<TimeProvider>()));
            return services;
        }
    }
}
