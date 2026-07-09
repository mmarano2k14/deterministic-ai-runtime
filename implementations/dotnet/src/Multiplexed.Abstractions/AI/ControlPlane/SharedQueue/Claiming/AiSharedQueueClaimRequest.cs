namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming
{
    /// <summary>
    /// Represents a request to atomically claim a pending shared queue item.
    /// </summary>
    public sealed class AiSharedQueueClaimRequest
    {
        /// <summary>
        /// Runtime instance id attempting to claim a pending shared queue item.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets or sets the logical control-plane identifier used to scope the shared queue claim.
        /// </summary>
        public string? ControlPlaneId { get; init; }

        /// <summary>
        /// Gets or sets the metadata used to resolve logical control-plane scope and enrich claim diagnostics.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Optional worker id or controller id attempting the claim.
        /// </summary>
        public string? WorkerId { get; init; }

        /// <summary>
        /// Optional tenant id used to restrict claim selection.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Optional pipeline key used to restrict claim selection.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Claim lease duration.
        /// </summary>
        public TimeSpan ClaimTtl { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Optional id used to correlate logs, metrics, traces, ledger entries,
        /// and dashboard actions across the control plane.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Optional reason explaining why claim was requested.
        /// </summary>
        public string? Reason { get; init; }
    }
}