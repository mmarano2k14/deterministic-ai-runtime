using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.McpServer.DependencyInjection;
using Multiplexed.AI.McpServer.Invocation.Outbound;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    /// <summary>Metadata integrity is checked before HTTP. Only a read-only loopback echo tool is invoked.</summary>
    [Collection(OutboundMcpTestCollection.Name)]
    public sealed class AiMcpEffectTransportTests
    {
        [Fact]
        public async Task Versioned_Effect_Metadata_Is_Internal_Not_An_Implicit_Tool_Idempotency_Protocol()
        {
            await using var server = await RecordingServer.StartAsync();
            using var provider = Provider(server.Endpoint);
            var request = AiMcpEffectIdentityTests.WithEffect(AiMcpEffectIdentityTests.Request("{\"value\":\"read-only\"}"));
            var response = await provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request);
            Assert.Equal(request.RequestId, response.GetProperty("requestId").GetString());
            Assert.Equal(1, response.GetProperty("schemaVersion").GetInt32()); // Result mapping is unchanged.
            Assert.False(response.GetProperty("isError").GetBoolean());
            var call = Assert.Single(server.ToolCalls);
            var parameters = call.GetProperty("params");
            Assert.Equal("probe.echo", parameters.GetProperty("name").GetString());
            var arguments = parameters.GetProperty("arguments");
            Assert.Equal("read-only", arguments.GetProperty("value").GetString());
            Assert.Single(arguments.EnumerateObject());
            Assert.DoesNotContain(request.Effect!.EffectId, call.GetRawText());
            Assert.DoesNotContain(request.Effect.RequestDigest, call.GetRawText());
        }

        [Fact]
        public async Task Two_Explicit_Read_Only_Attempts_Are_Not_Falsely_Deduplicated_By_Metadata()
        {
            await using var server = await RecordingServer.StartAsync();
            using var provider = Provider(server.Endpoint);
            var request = AiMcpEffectIdentityTests.WithEffect(AiMcpEffectIdentityTests.Request("{\"value\":\"same\"}"));
            var transport = provider.GetRequiredService<IAiMcpToolTransport>();
            await transport.InvokeAsync(request);
            await transport.InvokeAsync(request with { RequestId = "attempt-2" });
            Assert.Equal(2, server.ToolCalls.Count);
        }

        [Theory]
        [InlineData("missing-effect")]
        [InlineData("effect-id")]
        [InlineData("digest")]
        [InlineData("effect-version")]
        [InlineData("request-version")]
        [InlineData("ambiguous-legacy")]
        [InlineData("arguments")]
        [InlineData("tenant")]
        [InlineData("revision")]
        [InlineData("tool")]
        public async Task Invalid_Effect_Envelope_Is_Refused_Before_Any_Http_Request(string failure)
        {
            await using var server = await RecordingServer.StartAsync();
            using var provider = Provider(server.Endpoint);
            var request = AiMcpEffectIdentityTests.WithEffect(AiMcpEffectIdentityTests.Request("{\"value\":\"x\"}"));
            request = failure switch
            {
                "missing-effect" => request with { Effect = null },
                "effect-id" => request with { Effect = request.Effect! with { EffectId = "forged" } },
                "digest" => request with { Effect = request.Effect! with { RequestDigest = "forged" } },
                "effect-version" => request with { Effect = request.Effect! with { SchemaVersion = 99 } },
                "request-version" => request with { SchemaVersion = 99 },
                "ambiguous-legacy" => request with { SchemaVersion = 1 },
                "arguments" => request with { Arguments = AiMcpEffectIdentityTests.Json("{\"value\":\"changed\"}") },
                "tenant" => request with { Context = request.Context with { TenantId = "other" } },
                "revision" => request with { ConnectionRevision = "revision-2" },
                _ => request with { Tool = "probe.other" }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request));
            Assert.Equal(0, server.RequestCount);
        }

        [Fact]
        public async Task A_Recomputed_Digest_Does_Not_Bypass_The_Exact_Connection_Catalog()
        {
            await using var server = await RecordingServer.StartAsync();
            using var provider = Provider(server.Endpoint);
            var request = AiMcpEffectIdentityTests.WithEffect(AiMcpEffectIdentityTests.Request("{\"value\":\"x\"}")
                with { ConnectionRevision = "revision-2" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request));
            Assert.Equal(0, server.RequestCount);
        }

        [Fact]
        public async Task Historical_Effectless_Internal_Envelope_Remains_An_Explicit_Compatibility_Path()
        {
            await using var server = await RecordingServer.StartAsync();
            using var provider = Provider(server.Endpoint);
            var request = AiMcpEffectIdentityTests.Request("{\"value\":\"legacy\"}") with { SchemaVersion = 1 };
            var response = await provider.GetRequiredService<IAiMcpToolTransport>().InvokeAsync(request);
            Assert.False(response.GetProperty("isError").GetBoolean());
            Assert.Single(server.ToolCalls);
        }

        private static ServiceProvider Provider(Uri endpoint)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAiOutboundMcpToolExecution(new AiOutboundMcpToolExecutionOptions
            {
                AllowUnencryptedLoopback = true,
                ConnectionTimeout = TimeSpan.FromSeconds(3),
                Connections = [new AiOutboundMcpConnectionRegistration
                {
                    TenantId = "tenant-a", TenantGroupId = "group-a", ConnectionRef = "connection-a",
                    Revision = "revision-1", Endpoint = endpoint,
                    Tools = [new AiOutboundMcpToolRegistration("probe.echo", "reports", "publish", "invoke")]
                }]
            });
            return services.BuildServiceProvider();
        }

        // Measures all network entry, not merely successful tools/call responses.
        private sealed class RecordingServer : IAsyncDisposable
        {
            private readonly WebApplication _application;
            private int _requestCount;
            internal Uri Endpoint { get; private set; } = null!;
            internal int RequestCount => Volatile.Read(ref _requestCount);
            internal ConcurrentQueue<JsonElement> ToolCalls { get; } = new();
            private RecordingServer(WebApplication application) => _application = application;

            internal static async Task<RecordingServer> StartAsync()
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
                builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true)
                    .WithTools<OutboundMcpTestTools>();
                var app = builder.Build();
                var fixture = new RecordingServer(app);
                app.Use(async (context, next) =>
                {
                    Interlocked.Increment(ref fixture._requestCount);
                    if (HttpMethods.IsPost(context.Request.Method))
                    {
                        context.Request.EnableBuffering();
                        using var reader = new StreamReader(context.Request.Body, System.Text.Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                        var text = await reader.ReadToEndAsync();
                        context.Request.Body.Position = 0;
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            using var document = JsonDocument.Parse(text);
                            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                                document.RootElement.TryGetProperty("method", out var method) && method.GetString() == "tools/call")
                                fixture.ToolCalls.Enqueue(document.RootElement.Clone());
                        }
                    }
                    await next(context);
                });
                app.MapMcp("/mcp");
                try
                {
                    await app.StartAsync();
                    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                        .Addresses.Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
                    fixture.Endpoint = new Uri(address.TrimEnd('/') + "/mcp");
                    return fixture;
                }
                catch { await app.DisposeAsync(); throw; }
            }

            public async ValueTask DisposeAsync()
            { try { await _application.StopAsync(); } finally { await _application.DisposeAsync(); } }
        }
    }
}
