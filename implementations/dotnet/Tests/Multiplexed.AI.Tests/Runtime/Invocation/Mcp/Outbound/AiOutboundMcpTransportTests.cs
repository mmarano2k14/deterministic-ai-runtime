using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Invocation.Outbound;
using Multiplexed.AI.Runtime.Invocation.Mcp.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Real Streamable HTTP MCP calls through the official client transport.</summary>
    [Collection(OutboundMcpTestCollection.Name)]
    public sealed class AiOutboundMcpTransportTests
    {
        [Fact]
        public async Task Real_Streamable_Http_Call_Forwards_Arguments_And_Normalizes_Structured_Content()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var transport = provider.GetRequiredService<IAiMcpToolTransport>();
            var response = await transport.InvokeAsync(Request("probe.echo", Json("""{"value":"hello"}""")));
            Assert.Equal(1, response.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("request-1", response.GetProperty("requestId").GetString());
            Assert.False(response.GetProperty("isError").GetBoolean());
            Assert.True(response.GetProperty("content").GetArrayLength() >= 1);
            var structured = response.GetProperty("structuredContent");
            Assert.Equal("hello", Property(structured, "value").GetString());
            Assert.Equal("server", Property(structured, "source").GetString());
        }

        [Fact]
        public async Task Real_Transport_Marks_The_Business_Boundary_Before_Tools_Call()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var transport = Assert.IsAssignableFrom<IAiMcpDispatchBoundaryAwareTransport>(
                provider.GetRequiredService<IAiMcpToolTransport>());
            var boundary = new BoundaryProbe();

            var response = await transport.InvokeAsync(
                Request("probe.echo", Json("""{"value":"boundary"}""")), boundary);

            Assert.True(boundary.PossiblySent);
            Assert.False(response.GetProperty("isError").GetBoolean());
        }

        [Fact]
        public async Task Exact_Target_Rejection_Does_Not_Mark_The_Business_Boundary()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var transport = Assert.IsAssignableFrom<IAiMcpDispatchBoundaryAwareTransport>(
                provider.GetRequiredService<IAiMcpToolTransport>());
            var boundary = new BoundaryProbe();
            var request = Request("probe.echo", Json("""{"value":"x"}""")) with
            {
                ConnectionRevision = "revision-2"
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                transport.InvokeAsync(request, boundary));
            Assert.False(boundary.PossiblySent);
        }

        [Fact]
        public async Task Server_Owned_Header_Is_Applied_Without_Appearing_In_Request_Envelope()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var request = Request("probe.echo", Json("""{"value":"authorized"}"""));
            var serialized = JsonSerializer.Serialize(request);
            Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
            var result = await provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request);
            Assert.False(result.GetProperty("isError").GetBoolean());
        }

        [Fact]
        public async Task Tool_Error_Is_Normalized_And_Not_Retried()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.count"));
            var result = await provider.GetRequiredService<IAiMcpToolTransport>()
                .InvokeAsync(Request("probe.count", Json("{}")));
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Equal(1, OutboundMcpTestServer.CountCalls);
            var text = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Equal("attempt:1", text);
        }

        [Fact]
        public async Task Explicit_Tool_Error_Remains_A_Tool_Result_Not_A_Transport_Success_Fabrication()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.fail"));
            var result = await provider.GetRequiredService<IAiMcpToolTransport>()
                .InvokeAsync(Request("probe.fail", Json("{}")));
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Contains("tool refused", result.GetProperty("content").GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("revision")]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("connection")]
        [InlineData("tool")]
        public async Task Transport_Revalidates_Exact_Server_Target_Before_Network(string mismatch)
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var request = Request("probe.echo", Json("""{"value":"x"}"""));
            request = mismatch switch
            {
                "revision" => request with { ConnectionRevision = "revision-2" },
                "tenant" => request with { Context = request.Context with { TenantId = "other" } },
                "group" => request with { Context = request.Context with { TenantGroupId = "other" } },
                "connection" => request with { ConnectionRef = "other" },
                _ => request with { Tool = "other" }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request));
        }

        [Fact]
        public async Task Expired_Deadline_Is_Refused_Before_Opening_A_Client_Session()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"));
            var request = Request("probe.echo", Json("""{"value":"x"}""")) with
            {
                DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
            };
            await Assert.ThrowsAsync<TimeoutException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request));
        }

        [Fact]
        public async Task Caller_Cancellation_Interrupts_One_Real_Tool_Call_Without_Retry()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.slow"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(
                    Request("probe.slow", Json("""{"milliseconds":5000}""")), cancellation.Token));
        }


        [Fact]
        public async Task Request_Deadline_Is_Enforced_By_The_Transport_Even_Without_Caller_Cancellation()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.slow"));
            var request = Request("probe.slow", Json("""{"milliseconds":5000}""")) with
            {
                DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(250)
            };
            await Assert.ThrowsAsync<TimeoutException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request));
        }

        [Fact]
        public async Task Normalized_Response_Size_Is_Bounded_After_Protocol_Deserialization()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.large"), maximumNormalizedResponseBytes: 1024);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(
                    Request("probe.large", Json("""{"size":4096}"""))));
        }

        [Fact]
        public async Task Missing_Server_Header_Fails_As_A_Transport_Error()
        {
            await using var server = await OutboundMcpTestServer.StartAsync();
            using var provider = Provider(server.Endpoint, Tool("probe.echo"), includeHeader: false);
            await Assert.ThrowsAnyAsync<Exception>(() => provider.GetRequiredService<IAiMcpToolTransport>()
                .InvokeAsync(Request("probe.echo", Json("""{"value":"x"}"""))));
        }

        private static ServiceProvider Provider(
            Uri endpoint,
            AiOutboundMcpToolRegistration tool,
            bool includeHeader = true,
            int maximumNormalizedResponseBytes = 65536)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAiOutboundMcpToolExecution(new AiOutboundMcpToolExecutionOptions
            {
                AllowUnencryptedLoopback = true,
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                MaximumNormalizedResponseBytes = maximumNormalizedResponseBytes,
                Connections = [new AiOutboundMcpConnectionRegistration
                {
                    TenantId = "tenant",
                    TenantGroupId = "group",
                    ConnectionRef = "connection-a",
                    Revision = "revision-1",
                    Endpoint = endpoint,
                    Headers = includeHeader
                        ? new Dictionary<string, string> { ["X-Test-Server-Key"] = "secret" }
                        : new Dictionary<string, string>(),
                    Tools = [tool]
                }]
            });
            return services.BuildServiceProvider();
        }

        private static AiOutboundMcpToolRegistration Tool(string name) =>
            new(name, "reports", "publish", "invoke");

        private static AiMcpToolRequest Request(string tool, JsonElement arguments) => new(
            1,
            "request-1",
            DateTimeOffset.UtcNow.AddSeconds(10),
            new AiMcpToolInvocationContext("tenant", "group", "execution", "pipeline", "1", "step", "mcp"),
            "connection-a",
            "revision-1",
            tool,
            arguments);

        private static JsonElement Json(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static JsonElement Property(JsonElement value, string name) =>
            value.EnumerateObject().Single(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

        private sealed class BoundaryProbe : IAiMcpDispatchBoundary
        {
            private int _possiblySent;
            internal bool PossiblySent => Volatile.Read(ref _possiblySent) != 0;
            public void MarkPossiblySent() => Interlocked.Exchange(ref _possiblySent, 1);
        }
    }
}
