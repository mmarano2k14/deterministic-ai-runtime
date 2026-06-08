namespace Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker
{
    /// <summary>
    /// Represents an immutable visibility snapshot of the local runtime pipeline queue.
    ///
    /// This model is intended for control-plane, dashboard, MCP, HTTP API,
    /// CLI, diagnostics, and future Kubernetes visibility.
    /// </summary>
    public sealed class AiRuntimePipelineQueueState
    {
        /// <summary>
        /// Runtime instance id that owns this local queue.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Indicates whether the local queue is currently paused.
        /// </summary>
        public bool IsPaused { get; init; }

        /// <summary>
        /// Number of runs currently queued locally.
        /// </summary>
        public int QueuedRunCount { get; init; }

        /// <summary>
        /// Number of runs currently running locally.
        /// </summary>
        public int RunningRunCount { get; init; }

        /// <summary>
        /// Number of active runs known by the local runtime controller.
        /// This may include queued and running runs depending on implementation.
        /// </summary>
        public int ActiveRunCount { get; init; }

        /// <summary>
        /// Maximum local queue capacity when known.
        /// </summary>
        public int? QueueCapacity { get; init; }

        /// <summary>
        /// Maximum number of runs allowed to execute concurrently on this runtime instance.
        /// </summary>
        public int? MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Number of currently available local execution slots when known.
        /// </summary>
        public int? AvailableRunSlots { get; init; }

        /// <summary>
        /// Indicates whether this runtime instance can accept at least one new local run.
        /// </summary>
        public bool CanAcceptRun { get; init; }

        /// <summary>
        /// Total number of local workers configured for this runtime instance.
        /// </summary>
        public int? WorkerCount { get; init; }

        /// <summary>
        /// Number of local workers currently assigned to active executions.
        /// </summary>
        public int? ActiveWorkerCount { get; init; }

        /// <summary>
        /// Number of local workers currently available on this runtime instance.
        /// </summary>
        public int? AvailableWorkerCount { get; init; }

        /// <summary>
        /// Maximum number of local workers allowed per execution.
        /// </summary>
        public int? MaxLocalWorkersPerExecution { get; init; }

        /// <summary>
        /// UTC timestamp when the snapshot was created.
        /// </summary>
        public DateTimeOffset SnapshotAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}