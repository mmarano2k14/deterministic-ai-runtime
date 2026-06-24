namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Represents a runtime execution recovery decision.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryDecision
    {
        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the shared run identifier when it can be resolved.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the tenant identifier.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the recovery action selected by reconciliation.
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Gets the reason explaining the recovery decision.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Gets a value indicating whether a recovery mutation was applied.
        /// </summary>
        public bool Changed { get; init; }
    }
}