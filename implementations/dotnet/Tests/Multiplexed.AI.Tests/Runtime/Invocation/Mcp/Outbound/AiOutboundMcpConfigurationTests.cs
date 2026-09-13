using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Invocation.Outbound;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Server-owned routing, security validation and explicit DI activation.</summary>
    public sealed class AiOutboundMcpConfigurationTests
    {
        [Fact]
        public void Valid_Loopback_Configuration_Installs_Resolver_Transport_And_Mcp_Adapter()
        {
            var services = new ServiceCollection();
            services.AddAiOutboundMcpToolExecution(Options(new Uri("http://127.0.0.1:5100/mcp")));
            using var provider = services.BuildServiceProvider();
            Assert.NotNull(provider.GetRequiredService<IAiMcpToolResolver>());
            Assert.NotNull(provider.GetRequiredService<IAiMcpToolTransport>());
            Assert.Contains(provider.GetServices<IAiStepInvocationAdapterFactory>(), factory =>
                factory.GetType().Name == "AiMcpStepAdapterFactory");
        }

        [Fact]
        public void Existing_Mcp_Server_Registration_Does_Not_Implicitly_Enable_Outbound_Tools()
        {
            var services = new ServiceCollection();
            services.AddAiMcpServer();
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAiMcpToolResolver));
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAiMcpToolTransport));
        }

        [Theory]
        [InlineData("http://example.com/mcp", false)]
        [InlineData("http://127.0.0.1:5100/mcp", false)]
        [InlineData("http://127.0.0.1:5100/mcp", true)]
        [InlineData("https://example.com/mcp", false)]
        public void Endpoint_Scheme_Is_Server_Validated(string value, bool allowLoopback)
        {
            var options = Options(new Uri(value), allowLoopback: allowLoopback);
            var services = new ServiceCollection();
            if (value.StartsWith("https://", StringComparison.Ordinal) || allowLoopback)
            {
                services.AddAiOutboundMcpToolExecution(options);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => services.AddAiOutboundMcpToolExecution(options));
            }
        }

        [Theory]
        [InlineData("Host")]
        [InlineData("Content-Length")]
        [InlineData("Mcp-Session-Id")]
        [InlineData("MCP-Protocol-Version")]
        public void Protocol_And_Routing_Headers_Cannot_Be_Configured_As_Secrets(string header)
        {
            var options = Options(new Uri("https://example.com/mcp"), new Dictionary<string, string> { [header] = "x" });
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiOutboundMcpToolExecution(options));
        }

        [Theory]
        [InlineData("tenant", "other", "connection-a", "probe.echo")]
        [InlineData("tenant", "group", "other", "probe.echo")]
        [InlineData("tenant", "group", "connection-a", "other")]
        public async Task Catalog_Does_Not_Cross_Tenant_Connection_Or_Tool_Boundaries(
            string tenant, string group, string connection, string tool)
        {
            var services = new ServiceCollection();
            services.AddAiOutboundMcpToolExecution(Options(new Uri("https://example.com/mcp")));
            using var provider = services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<IAiMcpToolResolver>();
            var result = await resolver.ResolveAsync(new AiMcpToolResolutionRequest(tenant, group, connection, tool));
            Assert.Null(result);
        }

        [Fact]
        public async Task Resolver_Returns_Only_Opaque_Revision_And_Existing_Rbac_Capability()
        {
            var services = new ServiceCollection();
            services.AddAiOutboundMcpToolExecution(Options(new Uri("https://example.com/private/mcp"),
                new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }));
            using var provider = services.BuildServiceProvider();
            var binding = Assert.IsType<AiMcpToolBinding>(await provider.GetRequiredService<IAiMcpToolResolver>()
                .ResolveAsync(new AiMcpToolResolutionRequest("tenant", "group", "connection-a", "probe.echo")));
            Assert.Equal("revision-1", binding.ConnectionRevision);
            Assert.Equal("reports", binding.Resource);
            Assert.Equal("publish", binding.Feature);
            Assert.Equal("invoke", binding.Action);
            Assert.DoesNotContain("http", binding.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", binding.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Duplicate_Connection_And_Duplicate_Tool_Are_Rejected()
        {
            var connection = Connection(new Uri("https://example.com/mcp"));
            var duplicateConnection = new AiOutboundMcpToolExecutionOptions { Connections = [connection, connection] };
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiOutboundMcpToolExecution(duplicateConnection));

            var duplicateTool = Connection(new Uri("https://example.com/mcp"), tools: [Tool("probe.echo"), Tool("probe.echo")]);
            var duplicateToolOptions = new AiOutboundMcpToolExecutionOptions { Connections = [duplicateTool] };
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiOutboundMcpToolExecution(duplicateToolOptions));
        }

        [Fact]
        public void Existing_Resolver_Or_Transport_Cannot_Be_Silently_Replaced()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAiMcpToolResolver>(new NullResolver());
            Assert.Throws<InvalidOperationException>(() => services.AddAiOutboundMcpToolExecution(Options(new Uri("https://example.com/mcp"))));
        }

        private static AiOutboundMcpToolExecutionOptions Options(
            Uri endpoint,
            IReadOnlyDictionary<string, string>? headers = null,
            bool? allowLoopback = null) => new()
        {
            AllowUnencryptedLoopback = allowLoopback ?? (endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp),
            Connections = [Connection(endpoint, headers)]
        };

        private static AiOutboundMcpConnectionRegistration Connection(
            Uri endpoint,
            IReadOnlyDictionary<string, string>? headers = null,
            IReadOnlyList<AiOutboundMcpToolRegistration>? tools = null) => new()
        {
            TenantId = "tenant",
            TenantGroupId = "group",
            ConnectionRef = "connection-a",
            Revision = "revision-1",
            Endpoint = endpoint,
            Headers = headers ?? new Dictionary<string, string>(),
            Tools = tools ?? [Tool("probe.echo")]
        };

        private static AiOutboundMcpToolRegistration Tool(string name) =>
            new(name, "reports", "publish", "invoke");

        private sealed class NullResolver : IAiMcpToolResolver
        {
            public Task<AiMcpToolBinding?> ResolveAsync(AiMcpToolResolutionRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult<AiMcpToolBinding?>(null);
        }
    }
}
