using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Multiplexed.AI.McpServer.Host;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides an in-memory MCP server host for integration tests.
    /// </summary>
    public sealed class McpServerTestHost : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Configures the MCP server test host.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configurationBuilder =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["AiMcpHost:Mode"] = "ControlPlaneOnly",
                    ["AiMcpHost:Port"] = "5001",
                    ["AiMcpHost:EnableSharedQueuePump"] = "false",
                    ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                    ["AiMcpHost:EnableReplayTools"] = "true",
                    ["AiMcpHost:EnableObservabilityTools"] = "true",
                    ["ConnectionStrings:Redis"] = "localhost:6379",
                    ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017",
                    ["Mongo:DatabaseName"] = "multiplexed-ai-mcp-tests"
                };

                configurationBuilder.AddInMemoryCollection(values);
            });


        }
    }
}