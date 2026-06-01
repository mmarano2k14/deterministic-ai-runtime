using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace Multiplexed.AI.McpServer.Hosting
{
    /// <summary>
    /// Provides MCP server registration helpers for the AI control-plane host.
    /// </summary>
    public static class AiMcpServerConfiguration
    {
        /// <summary>
        /// Registers the MCP server HTTP transport and tool discovery for the AI control-plane host.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The same service collection instance.</returns>
        public static IServiceCollection AddAiMcpServerConfiguration(
            this IServiceCollection services)
        {
            services
                .AddMcpServer()
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithToolsFromAssembly();

            return services;
        }
    }
}