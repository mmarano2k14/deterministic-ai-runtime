namespace Multiplexed.Abstractions.AI.ControlPlane.Signals
{
    /// <summary>
    /// Represents a lightweight best-effort notification that durable runtime state changed.
    /// </summary>
    /// <remarks>
    /// A runtime signal is not an authoritative state record.
    /// Consumers must verify the corresponding durable store state before taking
    /// a critical action.
    ///
    /// Signals may be delayed, duplicated, or lost. Consumers must therefore retain
    /// a durable fallback read strategy.
    /// </remarks>
    public sealed class AiRuntimeSignal
    {
        /// <summary>
        /// Gets the signal type.
        /// </summary>
        public required AiRuntimeSignalType Type { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier.
        /// </summary>
        public required string ControlPlaneId { get; init; }

        /// <summary>
        /// Gets the optional tenant identifier.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the optional shared run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the optional local runtime run identifier.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the optional durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the optional runtime instance identifier.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the optional completed DAG step count.
        /// </summary>
        public int? CompletedStepCount { get; init; }

        /// <summary>
        /// Gets the optional total DAG step count.
        /// </summary>
        public int? TotalStepCount { get; init; }

        /// <summary>
        /// Gets the optional execution orchestration version.
        /// </summary>
        public int? ExecutionVersion { get; init; }

        /// <summary>
        /// Gets the UTC timestamp at which the signal was created.
        /// </summary>
        public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}