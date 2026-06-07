namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump
{
    /// <summary>
    /// Represents a request to execute one shared queue pump cycle.
    /// </summary>
    public sealed class AiSharedQueuePumpRequest
    {
        /// <summary>
        /// Runtime instance id that should claim and dispatch queued shared runs.
        /// </summary>
        public required string PumpRuntimeInstanceId { get; init; }

        /// <summary>
        /// Optional worker id or pump id.
        /// </summary>
        public string? PumpWorkerId { get; init; }

        /// <summary>
        /// Optional tenant filter.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Optional pipeline filter.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Optional maximum number of dispatches for this cycle.
        /// When null, pump options are used.
        /// </summary>
        public int? MaxDispatches { get; init; }

        /// <summary>
        /// Optional claim lease duration.
        /// When null, pump options are used.
        /// </summary>
        public TimeSpan? ClaimTtl { get; init; }

        /// <summary>
        /// Optional correlation id.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Optional requester identity.
        /// </summary>
        public string? RequestedBy { get; init; }

        /// <summary>
        /// Optional source label.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Optional reason.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}