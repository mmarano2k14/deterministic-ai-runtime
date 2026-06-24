using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership
{
    /// <summary>
    /// Represents the result of resolving shared run ownership from runtime execution identifiers.
    /// </summary>
    public sealed class AiSharedRunOwnershipResolutionResult
    {
        /// <summary>
        /// Gets a value indicating whether a matching shared run ownership record was resolved.
        /// </summary>
        public bool Resolved { get; init; }

        /// <summary>
        /// Gets the shared run identifier when resolved.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier.
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

        /// <summary>
        /// Gets the shared queue item status when available.
        /// </summary>
        public AiSharedQueueItemStatus? QueueStatus { get; init; }

        /// <summary>
        /// Gets the shared run status when available.
        /// </summary>
        public AiSharedRunStatus? SharedRunStatus { get; init; }

        /// <summary>
        /// Gets the claim token when available.
        /// </summary>
        public string? ClaimToken { get; init; }

        /// <summary>
        /// Gets a value indicating whether the resolved ownership is recoverable by the recovery reconciler.
        /// </summary>
        public bool CanRecover { get; init; }

        /// <summary>
        /// Gets the reason explaining the resolution result.
        /// </summary>
        public required string Reason { get; init; }
    }
}