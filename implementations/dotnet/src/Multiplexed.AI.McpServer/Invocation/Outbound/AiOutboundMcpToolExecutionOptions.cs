using System.Collections.ObjectModel;

namespace Multiplexed.AI.McpServer.Invocation.Outbound
{
    /// <summary>
    /// Server-side configuration for real outbound MCP tool execution. These values are
    /// host configuration, never pipeline input and never part of the external SDK contract.
    /// </summary>
    public sealed class AiOutboundMcpToolExecutionOptions
    {
        public IReadOnlyList<AiOutboundMcpConnectionRegistration> Connections { get; init; } =
            Array.Empty<AiOutboundMcpConnectionRegistration>();

        public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public bool AllowUnencryptedLoopback { get; init; }
        public int MaximumNormalizedResponseBytes { get; init; } = 65536;
    }

    /// <summary>
    /// One exact tenant-owned MCP endpoint revision. Endpoint and headers remain inside the
    /// trusted server catalog; the runtime binding exposes only the opaque connection reference.
    /// </summary>
    public sealed class AiOutboundMcpConnectionRegistration
    {
        public required string TenantId { get; init; }
        public required string TenantGroupId { get; init; }
        public required string ConnectionRef { get; init; }
        public required string Revision { get; init; }
        public required Uri Endpoint { get; init; }
        public bool Enabled { get; init; } = true;
        public IReadOnlyDictionary<string, string> Headers { get; init; } =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public IReadOnlyList<AiOutboundMcpToolRegistration> Tools { get; init; } =
            Array.Empty<AiOutboundMcpToolRegistration>();

        public override string ToString() => $"{TenantId}/{ConnectionRef}@{Revision}";
    }

    /// <summary>
    /// Declares the exact tool and the existing RBAC capability required to invoke it.
    /// </summary>
    public sealed record AiOutboundMcpToolRegistration(
        string Name,
        string Resource,
        string Feature,
        string Action);
}
