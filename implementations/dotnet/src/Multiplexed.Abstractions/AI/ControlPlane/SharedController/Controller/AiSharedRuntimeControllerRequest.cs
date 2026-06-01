using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller
{
    /// <summary>
    /// Represents an adapter-neutral shared runtime controller request.
    /// </summary>
    /// <remarks>
    /// The shared controller sits above admission, runtime instances, local runtime queues,
    /// and the future shared queue.
    ///
    /// It does not directly execute DAG steps.
    /// </remarks>
    public sealed class AiSharedRuntimeControllerRequest
    {
        /// <summary>
        /// Requested shared runtime controller operation.
        /// </summary>
        public required AiSharedRuntimeControllerOperation Operation { get; set; }

        /// <summary>
        /// Shared controller run identifier.
        /// Required for get and cancel operations.
        /// </summary>
        public string? SharedRunId { get; set; }

        /// <summary>
        /// Pipeline run request to submit through the shared runtime controller.
        /// Required for submit operations.
        /// </summary>
        public AiRuntimePipelineRunRequest? RunRequest { get; init; }

        /// <summary>
        /// Optional externally supplied run id.
        /// If omitted, the shared controller generates one.
        /// </summary>
        public string? RequestedSharedRunId { get; set; }

        /// <summary>
        /// Optional tenant id used for future tenant-aware admission and routing policies.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Optional pipeline key or pipeline name used for policy and routing decisions.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Optional preferred runtime instance id.
        /// Admission may try this runtime instance first when it is available.
        /// </summary>
        public string? PreferredRuntimeInstanceId { get; init; }

        /// <summary>
        /// Indicates whether cancelled shared runs should be included when listing runs.
        /// </summary>
        public bool IncludeCancelled { get; init; }

        /// <summary>
        /// Indicates whether completed shared runs should be included when listing runs.
        /// </summary>
        public bool IncludeCompleted { get; init; }

        /// <summary>
        /// Indicates whether failed shared runs should be included when listing runs.
        /// </summary>
        public bool IncludeFailed { get; init; }

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
        /// Optional reason explaining why the shared controller operation was requested.
        /// Useful for audit, diagnostics, and operator history.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Indicates whether diagnostics should be included when available.
        /// </summary>
        public bool IncludeDiagnostics { get; init; } = true;

        /// <summary>
        /// Optional metadata for future tenant, priority, routing, dashboard,
        /// Kubernetes, or shared queue labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}