using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.AI.Runtime.Invocation.Mcp;

namespace Multiplexed.AI.McpServer.Invocation.Outbound
{
    /// <summary>
    /// Real Streamable HTTP MCP client. It performs exactly one tools/call attempt through
    /// ModelContextProtocol 1.3.x and normalizes CallToolResult into the runtime envelope.
    /// No automatic business retry or endpoint discovery is introduced here.
    /// </summary>
    internal sealed class AiOutboundMcpToolTransport : IAiMcpToolTransport
    {
        private readonly AiOutboundMcpConnectionCatalog _catalog;
        private readonly AiOutboundMcpHttpClientPool _httpClients;
        private readonly AiOutboundMcpToolExecutionOptions _options;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly TimeProvider _timeProvider;

        public AiOutboundMcpToolTransport(
            AiOutboundMcpConnectionCatalog catalog,
            AiOutboundMcpHttpClientPool httpClients,
            AiOutboundMcpToolExecutionOptions options,
            ILoggerFactory? loggerFactory = null,
            TimeProvider? timeProvider = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _httpClients = httpClients ?? throw new ArgumentNullException(nameof(httpClients));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _loggerFactory = loggerFactory;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<JsonElement> InvokeAsync(
            AiMcpToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = request.DeadlineUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("MCP request deadline expired before transport execution.");
            }

            if (!_catalog.TryResolveTransportTarget(
                request.Context.TenantId,
                request.Context.TenantGroupId,
                request.ConnectionRef,
                request.ConnectionRevision,
                request.Tool,
                out var target) || target is null)
            {
                throw new InvalidOperationException(
                    "The exact server-owned MCP connection revision/tool is unavailable.");
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(remaining);
            try
            {
                using var httpClient = _httpClients.CreateClient();
                var transportOptions = new HttpClientTransportOptions
                {
                    Name = $"ai-mcp:{target.ConnectionRef}@{target.Revision}",
                    Endpoint = target.Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = _options.ConnectionTimeout,
                    AdditionalHeaders = target.Headers.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase)
                };

                await using var clientTransport = new HttpClientTransport(
                    transportOptions,
                    httpClient,
                    _loggerFactory,
                    ownsHttpClient: false);
                await using var client = await McpClient.CreateAsync(
                    clientTransport,
                    loggerFactory: _loggerFactory,
                    cancellationToken: deadline.Token).ConfigureAwait(false);

                var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in request.Arguments.EnumerateObject())
                {
                    arguments.Add(property.Name, property.Value.Clone());
                }

                var result = await client.CallToolAsync(
                    target.Tool,
                    arguments,
                    cancellationToken: deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                return Normalize(result, request.RequestId);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "MCP invocation exceeded its server deadline; the remote outcome may be unknown.", ex);
            }
        }

        private JsonElement Normalize(ModelContextProtocol.Protocol.CallToolResult result, string requestId)
        {
            ArgumentNullException.ThrowIfNull(result);
            var content = JsonSerializer.SerializeToElement(result.Content, McpJsonUtilities.DefaultOptions);
            if (content.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("MCP client returned malformed content.");
            }
            if (result.StructuredContent is { } structured && structured.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("MCP structured content must be a JSON object.");
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("requestId", requestId);
                writer.WriteBoolean("isError", result.IsError is true);
                writer.WritePropertyName("content");
                content.WriteTo(writer);
                if (result.StructuredContent is { } structuredContent)
                {
                    writer.WritePropertyName("structuredContent");
                    structuredContent.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.Flush();
            }

            if (stream.Length > _options.MaximumNormalizedResponseBytes)
            {
                throw new InvalidOperationException(
                    $"MCP normalized response exceeds {_options.MaximumNormalizedResponseBytes} UTF-8 bytes.");
            }
            using var document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }

        private static void ValidateRequest(AiMcpToolRequest request) =>
            AiMcpEffectIdentities.ValidateRequest(request);
    }
}
