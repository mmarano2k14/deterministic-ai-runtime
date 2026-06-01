using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.McpServer.Hosting;
using Multiplexed.AI.McpServer.Tools;

namespace Multiplexed.AI.McpServer.DependencyInjection
{
    /// <summary>
    /// Provides dependency injection registration extensions for the MCP control-plane host.
    /// </summary>
    public static class AiMcpServerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers MCP server services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection instance.</returns>
        public static IServiceCollection AddAiMcpServer(
            this IServiceCollection services)
        {
            services.AddAiMcpServerConfiguration();

            services.AddSingleton<SharedRunMcpTools>();
            services.AddSingleton<SharedQueueMcpTools>();
            services.AddSingleton<RuntimeInstanceMcpTools>();
            services.AddSingleton<ReplayMcpTools>();
            services.AddSingleton<ExecutionControlMcpTools>();
            services.AddSingleton<RuntimeQueueMcpTools>();
            services.AddSingleton<ObservabilityMcpTools>();

            return services;
        }

        /// <summary>
        /// Registers MCP control-plane host services.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">The MCP control-plane host configuration delegate.</param>
        /// <returns>The same service collection instance.</returns>
        public static IServiceCollection AddAiMcpControlPlaneHost(
            this IServiceCollection services,
            Action<AiMcpControlPlaneHostOptions>? configure = null)
        {
            if (configure is not null)
            {
                services.Configure(configure);
            }

            services.AddHostedService<AiMcpControlPlaneBackgroundService>();

            return services;
        }
    }
}