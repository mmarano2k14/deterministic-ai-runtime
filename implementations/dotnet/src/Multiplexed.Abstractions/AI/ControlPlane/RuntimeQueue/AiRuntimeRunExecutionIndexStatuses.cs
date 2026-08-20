namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Defines canonical persisted statuses for runtime run execution index entries.
    /// </summary>
    public static class AiRuntimeRunExecutionIndexStatuses
    {
        /// <summary>The runtime run is queued.</summary>
        public const string Queued = "queued";
        /// <summary>The runtime run is running.</summary>
        public const string Running = "running";
        /// <summary>The runtime run is durably waiting.</summary>
        public const string Waiting = "waiting";
        /// <summary>The runtime run completed successfully.</summary>
        public const string Completed = "completed";
        /// <summary>The runtime run failed.</summary>
        public const string Failed = "failed";
        /// <summary>The runtime run was cancelled.</summary>
        public const string Cancelled = "cancelled";
        /// <summary>The runtime run has been released and requeued for recovery.</summary>
        public const string RequeuedForRecovery = "requeued-for-recovery";
    }
}
