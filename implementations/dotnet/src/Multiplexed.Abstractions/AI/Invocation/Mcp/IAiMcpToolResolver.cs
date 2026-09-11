namespace Multiplexed.Abstractions.AI.Invocation.Mcp
{
    /// <summary>
    /// Trusted server extension that resolves connection metadata without calling a tool.
    /// A missing, disabled or foreign-tenant reference must return null or fail. It must
    /// not fetch arbitrary URLs supplied as references or infer a grant from tool hints.
    /// No production connection catalog is installed by this contract.
    /// </summary>
    public interface IAiMcpToolResolver
    {
        Task<AiMcpToolBinding?> ResolveAsync(
            AiMcpToolResolutionRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Only the trusted tenant identity and declared logical target are projected.</summary>
    public sealed record AiMcpToolResolutionRequest(
        string TenantId,
        string TenantGroupId,
        string ConnectionRef,
        string Tool);
}
