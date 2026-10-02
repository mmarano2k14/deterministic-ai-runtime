using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.McpServer.Invocation.Outbound;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.McpServer.DependencyInjection
{
    /// <summary>
    /// Explicitly installs real outbound MCP step execution. Existing MCP server hosting does
    /// not enable this path automatically, and all connection material remains server-owned.
    /// </summary>
    public static class AiOutboundMcpToolExecutionServiceCollectionExtensions
    {
        public static IServiceCollection AddAiOutboundMcpToolExecution(
            this IServiceCollection services,
            AiOutboundMcpToolExecutionOptions options,
            AiMcpStepInvocationOptions? invocationOptions = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(options);
            if (services.Any(item => item.ServiceType == typeof(IAiMcpToolResolver)) ||
                services.Any(item => item.ServiceType == typeof(IAiMcpToolTransport)))
            {
                throw new InvalidOperationException("Outbound MCP tool execution is already configured.");
            }

            var catalog = new AiOutboundMcpConnectionCatalog(options);
            var httpClients = new AiOutboundMcpHttpClientPool(options);
            services.AddSingleton(options);
            services.AddSingleton(catalog);
            services.AddSingleton<IAiMcpToolResolver>(catalog);
            services.AddSingleton(httpClients);
            services.AddSingleton<AiOutboundMcpToolTransport>(provider => new AiOutboundMcpToolTransport(
                provider.GetRequiredService<AiOutboundMcpConnectionCatalog>(),
                provider.GetRequiredService<AiOutboundMcpHttpClientPool>(),
                provider.GetRequiredService<AiOutboundMcpToolExecutionOptions>(),
                provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                provider.GetService<TimeProvider>()));
            services.AddSingleton<IAiMcpToolTransport>(provider =>
            {
                var physical = provider.GetRequiredService<AiOutboundMcpToolTransport>();
                var journal = provider.GetService<AiMcpEffectEvidenceJournal>();
                return journal is null
                    ? physical
                    : new AiDurableMcpToolTransport(journal, physical);
            });

            if (services.Any(item => item.ServiceType == typeof(AiMcpStepInvocationOptions)))
            {
                if (invocationOptions is not null)
                {
                    throw new InvalidOperationException("MCP step invocation options are already configured.");
                }
            }
            else
            {
                services.AddSingleton(invocationOptions ?? new AiMcpStepInvocationOptions());
            }

            services.AddSingleton<AiMcpStepAdapterFactory>();
            services.AddSingleton<IAiStepInvocationAdapterFactory>(provider =>
                provider.GetRequiredService<AiMcpStepAdapterFactory>());
            return services;
        }
    }
}
