using System.Net.Http;
using Multiplexed.Abstractions.AI.Invocation.Mcp;

namespace Multiplexed.AI.McpServer.Invocation.Outbound
{
    /// <summary>
    /// Immutable server-side connection catalog. It resolves only configured tenant targets and
    /// never derives an endpoint, credential, revision or capability from pipeline data.
    /// </summary>
    internal sealed class AiOutboundMcpConnectionCatalog : IAiMcpToolResolver
    {
        private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host",
            "Content-Length",
            "Transfer-Encoding",
            "Connection",
            "Mcp-Session-Id",
            "MCP-Protocol-Version"
        };

        private readonly IReadOnlyDictionary<ConnectionKey, Connection> _connections;

        public AiOutboundMcpConnectionCatalog(AiOutboundMcpToolExecutionOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ValidateOptions(options);

            var connections = new Dictionary<ConnectionKey, Connection>();
            foreach (var registration in options.Connections)
            {
                var tools = new Dictionary<string, AiOutboundMcpToolRegistration>(StringComparer.Ordinal);
                foreach (var tool in registration.Tools)
                {
                    if (!tools.TryAdd(tool.Name, tool))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate MCP tool '{tool.Name}' for connection '{registration.ConnectionRef}'.");
                    }
                }

                var connection = new Connection(
                    registration.TenantId,
                    registration.TenantGroupId,
                    registration.ConnectionRef,
                    registration.Revision,
                    registration.Endpoint,
                    registration.Enabled,
                    registration.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                    tools);
                if (!connections.TryAdd(new ConnectionKey(
                    registration.TenantId,
                    registration.TenantGroupId,
                    registration.ConnectionRef), connection))
                {
                    throw new InvalidOperationException(
                        $"Duplicate MCP connection '{registration.ConnectionRef}' for the same tenant scope.");
                }
            }

            _connections = connections;
        }

        public Task<AiMcpToolBinding?> ResolveAsync(
            AiMcpToolResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_connections.TryGetValue(new ConnectionKey(
                request.TenantId,
                request.TenantGroupId,
                request.ConnectionRef), out var connection) ||
                !connection.Enabled ||
                !connection.Tools.TryGetValue(request.Tool, out var tool))
            {
                return Task.FromResult<AiMcpToolBinding?>(null);
            }

            return Task.FromResult<AiMcpToolBinding?>(new AiMcpToolBinding(
                connection.TenantId,
                connection.TenantGroupId,
                connection.ConnectionRef,
                tool.Name,
                connection.Revision,
                tool.Resource,
                tool.Feature,
                tool.Action));
        }

        public bool TryResolveTransportTarget(
            string tenantId,
            string tenantGroupId,
            string connectionRef,
            string revision,
            string tool,
            out TransportTarget? target)
        {
            target = null;
            if (!_connections.TryGetValue(new ConnectionKey(tenantId, tenantGroupId, connectionRef), out var connection) ||
                !connection.Enabled ||
                !string.Equals(connection.Revision, revision, StringComparison.Ordinal) ||
                !connection.Tools.ContainsKey(tool))
            {
                return false;
            }

            target = new TransportTarget(
                connection.Endpoint,
                connection.Headers,
                connection.ConnectionRef,
                connection.Revision,
                tool);
            return true;
        }

        private static void ValidateOptions(AiOutboundMcpToolExecutionOptions options)
        {
            if (options.ConnectionTimeout <= TimeSpan.Zero || options.ConnectionTimeout > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(nameof(options),
                    "MCP connection timeout must be positive and at most 30 seconds.");
            }
            if (options.MaximumNormalizedResponseBytes is < 1024 or > 4 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(options),
                    "MCP maximum normalized response size must be between 1024 and 4194304 bytes.");
            }
            if (options.Connections is null || options.Connections.Count == 0)
            {
                throw new InvalidOperationException("At least one outbound MCP connection is required.");
            }

            foreach (var connection in options.Connections)
            {
                ArgumentNullException.ThrowIfNull(connection);
                RequireSegment(connection.TenantId, nameof(connection.TenantId), 128);
                RequireSegment(connection.TenantGroupId, nameof(connection.TenantGroupId), 128);
                RequireReference(connection.ConnectionRef, nameof(connection.ConnectionRef));
                RequireReference(connection.Revision, nameof(connection.Revision));
                ValidateEndpoint(connection.Endpoint, options.AllowUnencryptedLoopback);
                ValidateHeaders(connection.Headers);
                if (connection.Tools is null || connection.Tools.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MCP connection '{connection.ConnectionRef}' must declare at least one tool.");
                }
                foreach (var tool in connection.Tools)
                {
                    ArgumentNullException.ThrowIfNull(tool);
                    RequireSegment(tool.Name, nameof(tool.Name), 128);
                    RequireSegment(tool.Resource, nameof(tool.Resource), 128);
                    RequireSegment(tool.Feature, nameof(tool.Feature), 128);
                    RequireSegment(tool.Action, nameof(tool.Action), 128);
                }
            }
        }

        private static void ValidateEndpoint(Uri? endpoint, bool allowUnencryptedLoopback)
        {
            if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Fragment.Length != 0 ||
                !string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new InvalidOperationException("MCP endpoint must be an absolute credential-free URI without a fragment.");
            }
            if (endpoint.Scheme == Uri.UriSchemeHttps)
            {
                return;
            }
            if (endpoint.Scheme == Uri.UriSchemeHttp && allowUnencryptedLoopback && endpoint.IsLoopback)
            {
                return;
            }
            throw new InvalidOperationException(
                "Outbound MCP endpoints require HTTPS; unencrypted HTTP is allowed only for explicit loopback development/test configuration.");
        }

        private static void ValidateHeaders(IReadOnlyDictionary<string, string>? headers)
        {
            if (headers is null)
            {
                throw new InvalidOperationException("MCP headers collection cannot be null.");
            }
            if (headers.Count > 32)
            {
                throw new InvalidOperationException("MCP connection has too many server-owned headers.");
            }

            foreach (var pair in headers)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || ReservedHeaders.Contains(pair.Key) ||
                    pair.Value is null || pair.Value.Length > 4096 || pair.Value.Any(char.IsControl))
                {
                    throw new InvalidOperationException("MCP connection contains an invalid or reserved HTTP header.");
                }
                using var probe = new HttpRequestMessage();
                if (!probe.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                {
                    throw new InvalidOperationException("MCP connection contains a header that cannot be applied to requests.");
                }
            }
        }

        private static void RequireSegment(string? value, string name, int maxLength)
        {
            if (value is null || value.Length == 0 || value.Length > maxLength ||
                value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            {
                throw new InvalidOperationException($"Invalid MCP {name}.");
            }
        }

        private static void RequireReference(string? value, string name)
        {
            if (value is null || value.Length == 0 || value.Length > 256 || !char.IsAsciiLetterOrDigit(value[0]) ||
                value.Contains("..", StringComparison.Ordinal) ||
                value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/')))
            {
                throw new InvalidOperationException($"Invalid MCP {name}.");
            }
        }

        private readonly record struct ConnectionKey(string TenantId, string TenantGroupId, string ConnectionRef);

        private sealed record Connection(
            string TenantId,
            string TenantGroupId,
            string ConnectionRef,
            string Revision,
            Uri Endpoint,
            bool Enabled,
            IReadOnlyDictionary<string, string> Headers,
            IReadOnlyDictionary<string, AiOutboundMcpToolRegistration> Tools);

        internal sealed record TransportTarget(
            Uri Endpoint,
            IReadOnlyDictionary<string, string> Headers,
            string ConnectionRef,
            string Revision,
            string Tool);
    }
}
