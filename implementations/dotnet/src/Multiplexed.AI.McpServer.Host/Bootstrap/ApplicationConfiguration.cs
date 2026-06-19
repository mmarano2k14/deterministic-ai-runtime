using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Configures the HTTP application pipeline.
    /// </summary>
    public static class ApplicationConfiguration
    {
        /// <summary>
        /// Configures application endpoints according to the MCP host mode.
        /// </summary>
        /// <param name="app">The web application.</param>
        public static void Configure(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            var hostOptions =
                app.Services
                    .GetRequiredService<IOptions<AiMcpHostOptions>>()
                    .Value;

            Console.WriteLine($"[APP CONFIG] Mode='{hostOptions.Mode}'");

            app.MapHealthChecks("/health");

            switch (hostOptions.Mode)
            {
                case AiMcpHostMode.ControlPlaneOnly:
                case AiMcpHostMode.ControlPlaneWithLocalRuntimeInstances:
                case AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances:
                    Console.WriteLine("[APP CONFIG] Mapping MCP endpoint '/mcp'.");

                    app.UseAuthentication();

                    app.UseWhen(
                        context => context.Request.Path.StartsWithSegments("/mcp"),
                        branch =>
                        {
                            branch.UseMiddleware<ExecutionContextMiddleware>();
                            branch.UseMiddleware<NamespaceGuardMiddleware>();
                        });

                    app.UseAuthorization();

                    app.MapMcp("/mcp");

                    break;

                case AiMcpHostMode.RuntimeInstanceOnly:
                    Console.WriteLine("[APP CONFIG] Mapping runtime command endpoint '/runtime-instance/commands'.");
                    app.MapAiRuntimeInstanceHttpCommandEndpoint();
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported MCP host mode '{hostOptions.Mode}'.");
            }
        }
    }
}