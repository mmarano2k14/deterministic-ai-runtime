using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
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
        /// Gets the logical runtime pool identifier that owns this runtime capacity.
        /// </summary>
        /// <remarks>
        /// This is a first-class membership and placement identity. Capacity selection must not
        /// infer runtime pool membership from metadata.
        /// </remarks>
        public string? PoolId { get; init; }

        /// <summary>
        /// Gets the immutable identity of the exact host incarnation that publishes this capacity.
        /// </summary>
        /// <remarks>
        /// Several runtime capacity descriptors may share this value when independent runtime
        /// processes are hosted by the same runtime pool host.
        /// </remarks>
        public string? HostId { get; init; }

        /// <summary>
        /// Gets the tenant identifier that owns this runtime capacity, when tenant-scoped.
        /// </summary>
        /// <remarks>
        /// This value is a first-class routing and isolation field.
        /// Metadata may duplicate it for diagnostics, but tenant-aware capacity filtering
        /// must not depend only on metadata.
        /// </remarks>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier that owns this runtime capacity, when group-scoped.
        /// </summary>
        /// <remarks>
        /// This value is a first-class routing and isolation field.
        /// It allows dedicated or hybrid group-owned runtime capacity to be matched without
        /// relying only on metadata.
        /// </remarks>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the provider used to dispatch work to this runtime instance.
        /// </summary>
        /// <remarks>
        /// This is a first-class compatibility and routing field. Capacity selection
        /// must not infer provider compatibility from metadata.
        /// </remarks>
        public string? ProviderName { get; init; }

        /// <summary>
        /// Gets the tenant isolation mode published by this runtime instance.
        /// </summary>
        /// <remarks>
        /// This is a first-class selection and isolation field. Metadata may duplicate
        /// the value for diagnostics, but selection must use this property.
        /// </remarks>
        public AiRuntimeInstanceIsolationMode IsolationMode { get; init; } =
            AiRuntimeInstanceIsolationMode.Shared;

        /// <summary>
        /// Gets a value indicating whether shared capacity may be used when owned
        /// capacity is unavailable.
        /// </summary>
        public bool AllowSharedFallback { get; init; } = true;

        /// <summary>
        /// Gets a value indicating whether owned capacity should be preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; init; }

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
        /// Gets optional non-authoritative metadata for diagnostics, observability, provider labels,
        /// dashboards, zones, or deployment information.
        /// </summary>
        /// <remarks>
        /// Metadata must not control routing, membership, lifecycle, draining, capacity selection,
        /// or recovery. Any value required for correctness must be represented by a typed property.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets the control-plane host identity that owns or manages this runtime instance.
        /// </summary>
        public string? ControlPlaneHostId { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier that owns this capacity descriptor.
        /// </summary>
        public string? ControlPlaneId { get; init; }
    }
}