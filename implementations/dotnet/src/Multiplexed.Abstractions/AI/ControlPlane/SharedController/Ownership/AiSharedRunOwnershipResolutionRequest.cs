namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership
{
    /// <summary>
    /// Represents a request to resolve shared run ownership from runtime execution identifiers.
    /// </summary>
    public sealed class AiSharedRunOwnershipResolutionRequest
    {
        /// <summary>
        /// Gets the runtime instance identifier that owns or owned the local runtime run.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the local runtime queue run identifier.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable DAG execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the tenant identifier.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier.
        /// </summary>
        public string? TenantGroupId { get; init; }
    }
}