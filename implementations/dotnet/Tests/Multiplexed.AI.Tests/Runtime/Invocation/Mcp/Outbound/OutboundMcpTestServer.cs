using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Multiplexed.AI.Tests.Runtime.Invocation.OutboundMcp
{
    [CollectionDefinition(OutboundMcpTestCollection.Name, DisableParallelization = true)]
    public sealed class OutboundMcpTestCollection
    {
        public const string Name = "Outbound MCP HTTP transport";
    }

    /// <summary>Real loopback Streamable HTTP MCP server used only by outbound transport tests.</summary>
    internal sealed class OutboundMcpTestServer : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private OutboundMcpTestServer(WebApplication application, Uri endpoint)
        {
            _application = application;
            Endpoint = endpoint;
        }

        public Uri Endpoint { get; }
        public static int CountCalls => OutboundMcpTestTools.CountCalls;

        public static async Task<OutboundMcpTestServer> StartAsync(
            bool requireHeader = true,
            CancellationToken cancellationToken = default)
        {
            OutboundMcpTestTools.Reset();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services
                .AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<OutboundMcpTestTools>();

            var application = builder.Build();
            if (requireHeader)
            {
                application.Use(async (context, next) =>
                {
                    if (!context.Request.Headers.TryGetValue("X-Test-Server-Key", out var value) || value != "secret")
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                });
            }
            application.MapMcp("/mcp");
            await application.StartAsync(cancellationToken).ConfigureAwait(false);

            var server = application.Services.GetRequiredService<IServer>();
            var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single(value =>
                value.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("MCP test server did not expose its loopback address.");
            return new OutboundMcpTestServer(application, new Uri($"{address.TrimEnd('/')}/mcp"));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _application.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await _application.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deterministic test-only MCP tools used by the real HTTP transport proof.</summary>
    [McpServerToolType]
    internal sealed class OutboundMcpTestTools
    {
        private static int _countCalls;

        public static int CountCalls => Volatile.Read(ref _countCalls);
        public static void Reset() => Interlocked.Exchange(ref _countCalls, 0);

        [McpServerTool(Name = "probe.echo", UseStructuredContent = true)]
        [Description("Returns the supplied value as structured content.")]
        public static OutboundMcpProbeResult Echo(string value) => new(value, "server");

        [McpServerTool(Name = "probe.fail")]
        [Description("Returns one explicit MCP tool error.")]
        public static CallToolResult Fail() => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "tool refused" }]
        };

        [McpServerTool(Name = "probe.count")]
        [Description("Counts exact tools/call attempts.")]
        public static CallToolResult Count()
        {
            var count = Interlocked.Increment(ref _countCalls);
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"attempt:{count}" }]
            };
        }

        [McpServerTool(Name = "probe.slow")]
        [Description("Waits until cancelled or the delay elapses.")]
        public static async Task<string> Slow(int milliseconds, CancellationToken cancellationToken)
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return "completed";
        }

        [McpServerTool(Name = "probe.large")]
        [Description("Returns a large text payload for normalized-size validation.")]
        public static string Large(int size) => new('x', size);
    }

    internal sealed record OutboundMcpProbeResult(string Value, string Source);
}
