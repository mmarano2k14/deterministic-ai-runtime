namespace Multiplexed.Abstractions.AI.Invocation.Mcp
{
    /// <summary>
    /// Server-resolved routing metadata, not an authorization grant. The resolver must
    /// look up the exact tenant/connection/tool and supply its existing RBAC capability.
    /// No endpoint, credential or permission set is accepted from a pipeline definition.
    /// </summary>
    public sealed record AiMcpToolBinding(
        string TenantId,
        string TenantGroupId,
        string ConnectionRef,
        string Tool,
        string ConnectionRevision,
        string Resource,
        string Feature,
        string Action);
}
