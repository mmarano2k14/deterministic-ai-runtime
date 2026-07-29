using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.Abstractions.AI.ControlPlane.Admission
{
    /// <summary>
    /// Represents a request to admit a new run into the runtime control plane.
    ///
    /// Admission decides whether a run should be assigned to an available runtime instance,
    /// globally queued, rejected, or trigger a scale-out request.
    /// </summary>
    public sealed class AiRunAdmissionRequest
    {
        /// <summary>
        /// Pipeline run request that needs admission.
        /// </summary>
        public required AiRuntimePipelineRunRequest RunRequest { get; init; }

        /// <summary>
        /// Optional externally supplied run id.
        /// If omitted, a future shared queue may generate one.
        /// </summary>
        public string? RunId { get; init; }

        /// <summary>
        /// Optional tenant id used for future tenant-aware admission policies.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Optional pipeline key or pipeline name used for policy and routing decisions.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Optional preferred runtime instance id.
        /// When provided, admission may try this instance first if it is available.
        /// </summary>
        public string? PreferredRuntimeInstanceId { get; init; }

        /// <summary>
        /// Optional typed placement directive for this admission attempt.
        /// When omitted, the historical PreferredRuntimeInstanceId behavior is preserved.
        /// </summary>
        public AiRunPlacementDirective? Placement { get; init; }

        /// <summary>
        /// Optional id used to correlate logs, metrics, traces, ledger entries,
        /// and dashboard actions across the control plane.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Optional identity of the caller requesting admission.
        /// </summary>
        public string? RequestedBy { get; init; }

        /// <summary>
        /// Optional source adapter that initiated the request.
        /// Examples: http-api, mcp, cli, dashboard, kubernetes-control.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Optional reason explaining why admission was requested.
        /// Useful for audit and diagnostics.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional metadata for future tenant, priority, routing, dashboard, or Kubernetes labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}