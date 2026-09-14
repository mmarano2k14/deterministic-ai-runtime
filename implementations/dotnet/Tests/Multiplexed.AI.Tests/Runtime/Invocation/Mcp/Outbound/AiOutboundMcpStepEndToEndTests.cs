using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Invocation.Outbound;
using static Multiplexed.AI.Tests.Runtime.Invocation.McpStepTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Existing DAG adapter and RBAC path with the real outbound MCP transport behind it.</summary>
    [Collection(OutboundMcpTestCollection.Name)]
    public sealed class AiOutboundMcpStepEndToEndTests
    {
        [Fact]
        public async Task Existing_Mcp_Step_Path_Executes_One_Real_Remote_Tool_After_Rbac()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var outbound = Provider(server.Endpoint);
            var realResolver = outbound.GetRequiredService<IAiMcpToolResolver>();
            var realTransport = outbound.GetRequiredService<IAiMcpToolTransport>();
            var resolver = new Resolver { Handler = (request, token) => realResolver.ResolveAsync(request, token) };
            var transport = new Transport { Handler = (request, token) => realTransport.InvokeAsync(request, token) };
            using var fixture = await CreateAsync(
                McpStepTestSupport.Pipeline(Step(connection: "connection-a", tool: "probe.echo",
                    input: new Dictionary<string, object?> { ["value"] = "published" })),
                resolver,
                transport);

            var result = await fixture.InvokeAsync();

            Assert.True(result.Success);
            Assert.Single(resolver.Calls);
            Assert.Single(transport.Calls);
            var payload = Assert.IsType<System.Text.Json.JsonElement>(result.Value);
            var value = payload.EnumerateObject().Single(property =>
                string.Equals(property.Name, "value", StringComparison.OrdinalIgnoreCase)).Value;
            Assert.Equal("published", value.GetString());
        }

        [Fact]
        public async Task Existing_Rbac_Denial_Still_Blocks_Before_Arguments_And_Network()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var outbound = Provider(server.Endpoint);
            var realResolver = outbound.GetRequiredService<IAiMcpToolResolver>();
            var realTransport = outbound.GetRequiredService<IAiMcpToolTransport>();
            var resolver = new Resolver { Handler = (request, token) => realResolver.ResolveAsync(request, token) };
            var transport = new Transport { Handler = (request, token) => realTransport.InvokeAsync(request, token) };
            using var fixture = await CreateAsync(
                McpStepTestSupport.Pipeline(Step(connection: "connection-a", tool: "probe.echo",
                    input: new Dictionary<string, object?> { ["value"] = "must-not-run" })),
                resolver,
                transport,
                grant: string.Empty);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.InvokeAsync());
            Assert.Single(resolver.Calls);
            Assert.Empty(transport.Calls);
        }

        private static ServiceProvider Provider(Uri endpoint)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAiOutboundMcpToolExecution(new AiOutboundMcpToolExecutionOptions
            {
                AllowUnencryptedLoopback = true,
                Connections = [new AiOutboundMcpConnectionRegistration
                {
                    TenantId = "tenant-1",
                    TenantGroupId = "group-1",
                    ConnectionRef = "connection-a",
                    Revision = "revision-1",
                    Endpoint = endpoint,
                    Headers = new Dictionary<string, string> { ["X-Test-Server-Key"] = "secret" },
                    Tools = [new AiOutboundMcpToolRegistration("probe.echo", "reports", "publish", "invoke")]
                }]
            });
            return services.BuildServiceProvider();
        }
    }
}
