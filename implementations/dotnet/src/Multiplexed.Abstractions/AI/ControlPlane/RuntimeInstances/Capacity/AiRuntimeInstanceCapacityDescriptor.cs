using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Describes the visible local capacity of a runtime instance.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Represents the capacity view published by one runtime instance.
    /// - Allows a control-plane, dashboard, MCP server, or autoscaler to observe
    ///   runtime capacity across hosts, pods, or processes.
    ///
    /// IMPORTANT:
    /// - This descriptor is data-only.
    /// - It does not dispatch runs.
    /// - It does not replace local runtime queues.
    /// - Each runtime instance remains responsible for its own local workers and queue.
    /// </remarks>
    public sealed class AiRuntimeInstanceCapacityDescriptor
    {
        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the runtime instance role.
        /// </summary>
        public AiRuntimeInstanceRole Role { get; init; } =
            AiRuntimeInstanceRole.Runtime;

        /// <summary>
        /// Gets the runtime instance status.
        /// </summary>
        public AiRuntimeInstanceStatus Status { get; init; } =
            AiRuntimeInstanceStatus.Unknown;

        /// <summary>
        /// Gets the number of local workers owned by this runtime instance.
        /// </summary>
        public int WorkerCount { get; init; }

        /// <summary>
        /// Gets the number of local workers currently in use.
        /// </summary>
        public int ActiveWorkerCount { get; init; }

        /// <summary>
        /// Gets the number of local workers currently available.
        /// </summary>
        public int AvailableWorkerCount { get; init; }

        /// <summary>
        /// Gets the maximum number of workers that a single run may use.
        /// </summary>
        public int? MaxWorkersPerRun { get; init; }

        /// <summary>
        /// Gets the minimum number of available workers required to accept a new run.
        /// </summary>
        public int MinWorkersRequiredPerRun { get; init; } = 1;

        /// <summary>
        /// Gets the number of runs currently queued locally.
        /// </summary>
        public int QueuedRunCount { get; init; }

        /// <summary>
        /// Gets the number of runs currently running locally.
        /// </summary>
        public int RunningRunCount { get; init; }

        /// <summary>
        /// Gets the number of active runs known by the local runtime controller.
        /// </summary>
        public int ActiveRunCount { get; init; }

        /// <summary>
        /// Gets the maximum number of local runs that can execute concurrently.
        /// </summary>
        public int? MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Gets the maximum number of local run slots.
        /// </summary>
        public int? MaxRunSlots { get; init; }

        /// <summary>
        /// Gets the number of currently available local run slots.
        /// </summary>
        public int? AvailableRunSlots { get; init; }

        /// <summary>
        /// Gets the number of run slots reserved by admission but not yet visible in heartbeat.
        /// </summary>
        public int ReservedRunSlots { get; init; }

        /// <summary>
        /// Gets the number of effectively available run slots after reservations.
        /// </summary>
        public int? EffectiveAvailableRunSlots { get; init; }

        /// <summary>
        /// Gets a value indicating whether the local runtime queue is paused.
        /// </summary>
        public bool IsQueuePaused { get; init; }

        /// <summary>
        /// Gets a value indicating whether this runtime can accept a new run.
        /// </summary>
        public bool CanAcceptRun { get; init; }

        /// <summary>
        /// Gets the UTC timestamp of the last capacity heartbeat.
        /// </summary>
        public DateTimeOffset LastHeartbeatAtUtc { get; init; }

        /// <summary>
        /// Gets optional metadata for diagnostics, dashboard, tenant, zone, or Kubernetes labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}