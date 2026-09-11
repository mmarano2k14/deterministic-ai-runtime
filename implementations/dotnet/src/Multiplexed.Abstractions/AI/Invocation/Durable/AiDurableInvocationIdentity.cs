namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Identifies one logical custom operation. Generation changes only after an explicit
    /// durable business decision, never because a worker, transport attempt or lease changes.
    /// </summary>
    public sealed record AiDurableInvocationIdentity(
        string TenantId,
        string ExecutionId,
        string StepName,
        int Generation = 0);

    /// <summary>
    /// Trusted server ownership context, not a credential or an RBAC replacement.
    /// The caller must obtain this scope from the authenticated/restored runtime context.
    /// </summary>
    public sealed record AiDurableInvocationScope(
        string TenantId,
        string TenantGroupId,
        string ControlPlaneId);
}
