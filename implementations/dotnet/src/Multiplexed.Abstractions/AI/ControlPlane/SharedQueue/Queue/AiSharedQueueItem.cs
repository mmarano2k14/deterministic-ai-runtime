using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue
{
    /// <summary>
    /// Represents a pending or claimed shared queue item.
    /// </summary>
    /// <remarks>
    /// The shared queue does not own the full shared run record.
    /// It references the run by <see cref="SharedRunId"/>.
    ///
    /// Full shared run state is owned by IAiSharedRunStore.
    ///
    /// Tenant model:
    /// - ExecutionContextSnapshot.TenantId is the persistent tenant boundary used
    ///   for queue filtering, routing, dashboard visibility, and tenant isolation.
    /// - ExecutionContextSnapshot.ContextKey is volatile and is stored only for
    ///   traceability/debugging. It must not be used as a durable queue key,
    ///   execution key, or tenant partition key.
    /// </remarks>
    public sealed class AiSharedQueueItem
    {
        /// <summary>
        /// Shared controller run identifier.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier that owns this shared queue item.
        /// </summary>
        public string? ControlPlaneId { get; init; }

        /// <summary>
        /// Current shared queue item status.
        /// </summary>
        public required AiSharedQueueItemStatus Status { get; init; }

        /// <summary>
        /// Snapshot of the RBAC execution context associated with this queue item.
        /// </summary>
        /// <remarks>
        /// Tenant filtering must use <see cref="ExecutionContextSnapshot.TenantId"/>.
        /// Tenant group filtering must use <see cref="ExecutionContextSnapshot.TenantGroupId"/>.
        ///
        /// The snapshot context key is volatile and must not be used as a durable
        /// tenant partition key or queue identifier.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Optional pipeline key used for future routing and policy decisions.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Optional priority value.
        /// Lower values can be treated as higher priority by future implementations.
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Runtime instance id that claimed the item, when claimed.
        /// </summary>
        public string? ClaimedByRuntimeInstanceId { get; init; }

        /// <summary>
        /// Worker id or controller id that claimed the item, when available.
        /// </summary>
        public string? ClaimedByWorkerId { get; init; }

        /// <summary>
        /// Claim token assigned during atomic claim.
        /// </summary>
        public string? ClaimToken { get; init; }

        /// <summary>
        /// UTC timestamp when the item was enqueued.
        /// </summary>
        public DateTimeOffset EnqueuedAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp when the item was last updated.
        /// </summary>
        public DateTimeOffset UpdatedAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp when the item was claimed.
        /// </summary>
        public DateTimeOffset? ClaimedAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp when the claim expires.
        /// </summary>
        public DateTimeOffset? ClaimExpiresAtUtc { get; init; }

        /// <summary>
        /// Optional reason associated with the current item state.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional metadata for routing, dashboard, Kubernetes, or debugging.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}