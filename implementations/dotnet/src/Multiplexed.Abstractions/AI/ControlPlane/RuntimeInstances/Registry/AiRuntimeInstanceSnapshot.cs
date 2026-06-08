namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Represents an immutable visibility snapshot of a registered runtime instance.
    ///
    /// In Kubernetes, one runtime instance normally corresponds to one pod / replica.
    /// This model is intended for control-plane, dashboard, MCP, HTTP API,
    /// CLI, shared admission, autoscaling, and diagnostics.
    /// </summary>
    public sealed class AiRuntimeInstanceSnapshot
    {
        /// <summary>
        /// Runtime process / Kubernetes pod / replica identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Current runtime instance visibility status.
        /// </summary>
        public required AiRuntimeInstanceStatus Status { get; init; }

        /// <summary>
        /// Optional host name where the runtime instance is running.
        /// </summary>
        public string? HostName { get; init; }

        /// <summary>
        /// Optional process id for local diagnostics.
        /// </summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// Optional Kubernetes namespace when running inside Kubernetes.
        /// </summary>
        public string? KubernetesNamespace { get; init; }

        /// <summary>
        /// Optional Kubernetes pod name when running inside Kubernetes.
        /// </summary>
        public string? KubernetesPodName { get; init; }

        /// <summary>
        /// Optional Kubernetes node name when running inside Kubernetes.
        /// </summary>
        public string? KubernetesNodeName { get; init; }

        /// <summary>
        /// Number of local workers owned by this runtime instance.
        /// </summary>
        public int WorkerCount { get; init; }

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
        /// </summary>
        public int ActiveRunCount { get; init; }

        /// <summary>
        /// Maximum local queue capacity for this runtime instance.
        /// </summary>
        public int? QueueCapacity { get; init; }

        /// <summary>
        /// Maximum number of local runs that can execute concurrently on this runtime instance.
        /// </summary>
        public int? MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Number of currently available local execution slots.
        /// </summary>
        public int? AvailableRunSlots { get; init; }

        /// <summary>
        /// Indicates whether the local runtime queue is paused.
        /// </summary>
        public bool IsQueuePaused { get; init; }

        /// <summary>
        /// Indicates whether this runtime instance can accept at least one new local run.
        /// </summary>
        public bool CanAcceptRun { get; init; }

        /// <summary>
        /// UTC timestamp when this runtime instance was registered.
        /// </summary>
        public DateTimeOffset RegisteredAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp of the last heartbeat received from this runtime instance.
        /// </summary>
        public DateTimeOffset LastHeartbeatAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp when this snapshot was created.
        /// </summary>
        public DateTimeOffset SnapshotAtUtc { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Optional runtime version, package version, or build version.
        /// Useful for rolling upgrade diagnostics.
        /// </summary>
        public string? RuntimeVersion { get; init; }

        /// <summary>
        /// Optional metadata for dashboard, Kubernetes, tenant, zone, or deployment labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        public AiRuntimeInstanceRole Role { get; set; } = AiRuntimeInstanceRole.Runtime;

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
    }
}