using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control
{
    /// <summary>
    /// Represents an adapter-neutral runtime instance control-plane request.
    ///
    /// This request can be used later by HTTP API, MCP, CLI, dashboard,
    /// Kubernetes control-plane adapters, or shared admission components.
    /// </summary>
    public sealed class AiRuntimeInstanceControlPlaneRequest
    {
        /// <summary>
        /// Requested runtime instance control-plane operation.
        /// </summary>
        public required AiRuntimeInstanceControlPlaneOperation Operation { get; init; }

        /// <summary>
        /// Runtime instance identifier targeted by the operation.
        /// Required for heartbeat, get, drain, and unregister operations.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Runtime instance registration payload.
        /// Required for register operations.
        /// </summary>
        public AiRuntimeInstanceRegistration? Registration { get; init; }

        /// <summary>
        /// Number of runs currently queued locally.
        /// Used by heartbeat operations.
        /// </summary>
        public int QueuedRunCount { get; init; }

        /// <summary>
        /// Number of runs currently running locally.
        /// Used by heartbeat operations.
        /// </summary>
        public int RunningRunCount { get; init; }

        /// <summary>
        /// Number of active runs known by the local runtime controller.
        /// Used by heartbeat operations.
        /// </summary>
        public int ActiveRunCount { get; init; }

        /// <summary>
        /// Number of available local run slots.
        /// Used by heartbeat operations.
        /// </summary>
        public int? AvailableRunSlots { get; init; }

        /// <summary>
        /// Indicates whether the local runtime queue is paused.
        /// Used by heartbeat operations.
        /// </summary>
        public bool IsQueuePaused { get; init; }

        /// <summary>
        /// Indicates whether this runtime instance can accept a new local run.
        /// Used by heartbeat operations.
        /// </summary>
        public bool CanAcceptRun { get; init; }

        /// <summary>
        /// Runtime instance status to publish during heartbeat operations.
        /// </summary>
        public AiRuntimeInstanceStatus Status { get; init; } = AiRuntimeInstanceStatus.Unknown;

        /// <summary>
        /// Indicates whether stopped instances should be included when listing runtime instances.
        /// </summary>
        public bool IncludeStopped { get; init; }

        /// <summary>
        /// Optional id used to correlate logs, metrics, traces, ledger entries,
        /// and dashboard actions across the control plane.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Optional identity of the caller requesting the operation.
        /// This can be a user, service account, MCP caller, CLI user,
        /// dashboard operator, or Kubernetes control-plane component.
        /// </summary>
        public string? RequestedBy { get; init; }

        /// <summary>
        /// Optional source adapter that initiated the request.
        /// Examples: http-api, mcp, cli, dashboard, kubernetes-control.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Optional reason explaining why the runtime instance operation was requested.
        /// Useful for audit, diagnostics, and operator history.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Indicates whether diagnostics should be included when available.
        /// </summary>
        public bool IncludeDiagnostics { get; init; } = true;

        /// <summary>
        /// Gets or sets the number of local workers currently assigned to active executions.
        /// </summary>
        public int? ActiveWorkerCount { get; init; }

        /// <summary>
        /// Gets or sets the number of local workers currently available on this runtime instance.
        /// </summary>
        public int? AvailableWorkerCount { get; init; }

        /// <summary>
        /// Gets or sets the maximum number of local workers allowed per execution.
        /// </summary>
        public int? MaxLocalWorkersPerExecution { get; init; }
    }
}