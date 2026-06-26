namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes the execution context recovery and rehydration behavior.
    /// </summary>
    public sealed record AiRuntimeRecoveryContextInfo
    {
        /// <summary>
        /// Gets the context key from the shared execution context snapshot.
        /// </summary>
        public string? SnapshotContextKey { get; init; }

        /// <summary>
        /// Gets the context key from the durable execution record.
        /// </summary>
        public string? RecordContextKey { get; init; }

        /// <summary>
        /// Gets a value indicating whether the snapshot context key differs from the durable record context key.
        /// </summary>
        public bool ContextKeyMismatch { get; init; }

        /// <summary>
        /// Gets a value indicating whether the context was rehydrated using the durable execution identifier.
        /// </summary>
        public bool RehydratedByExecutionId { get; init; }

        /// <summary>
        /// Gets the reason associated with context rehydration.
        /// </summary>
        public string? RehydrationReason { get; init; }
    }
}