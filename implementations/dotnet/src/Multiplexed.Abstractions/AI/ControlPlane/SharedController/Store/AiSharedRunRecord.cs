using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Represents a shared runtime controller run record.
    /// </summary>
    /// <remarks>
    /// This record belongs to the shared controller lifecycle.
    /// It does not replace the local runtime queue run handle and it does not replace
    /// the durable DAG execution record.
    ///
    /// Identity separation:
    /// - SharedRunId: shared controller lifecycle id.
    /// - LocalRunId: local runtime queue/controller run id once dispatched.
    /// - ExecutionId: durable DAG execution id once created by a runtime instance.
    ///
    /// Tenant separation:
    /// - ExecutionContextSnapshot.TenantId is the persistent tenant boundary used
    ///   for filtering, storage isolation, dashboard views, and audit.
    /// - ExecutionContextSnapshot.ContextKey is volatile and must only be used
    ///   for traceability/debugging.
    /// </remarks>
    public sealed class AiSharedRunRecord
    {
        /// <summary>
        /// Shared controller run identifier.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Current shared run controller-level status.
        /// </summary>
        public required AiSharedRunStatus Status { get; init; }

        /// <summary>
        /// Original pipeline run request submitted to the shared controller.
        /// </summary>
        public required AiRuntimePipelineRunRequest RunRequest { get; init; }

        /// <summary>
        /// Snapshot of the RBAC execution context at the time this shared run
        /// was submitted or created.
        /// </summary>
        /// <remarks>
        /// Tenant filtering must use ExecutionContextSnapshot.TenantId.
        /// Tenant group filtering must use ExecutionContextSnapshot.TenantGroupId.
        ///
        /// ContextKey is volatile and must not be used as a durable tenant partition key,
        /// execution identifier, or orchestration key.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Optional local runtime queue run id once the shared run is dispatched
        /// to a selected runtime instance.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Optional durable DAG execution id once the selected runtime instance
        /// creates the execution.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Runtime instance selected by admission when available.
        /// </summary>
        public string? AssignedRuntimeInstanceId { get; init; }

        /// <summary>
        /// Admission decision produced for this shared run.
        /// </summary>
        public AiRunAdmissionDecision? AdmissionDecision { get; init; }

        /// <summary>
        /// Optional pipeline key or pipeline name used for policy and routing decisions.
        /// </summary>
        public string? PipelineKey { get; init; }

        /// <summary>
        /// Optional id used to correlate logs, metrics, traces, ledger entries,
        /// and dashboard actions across the control plane.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Optional identity of the caller requesting the shared run.
        /// </summary>
        public string? RequestedBy { get; init; }

        /// <summary>
        /// Optional source adapter that submitted the run.
        /// Examples: http-api, mcp, cli, dashboard, kubernetes-control.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Optional reason explaining why the shared run was submitted or changed.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional failure reason when the shared run fails or is rejected.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// UTC timestamp when the shared run was submitted.
        /// </summary>
        public DateTimeOffset SubmittedAtUtc { get; init; }

        /// <summary>
        /// UTC timestamp when the shared run was last updated.
        /// </summary>
        public DateTimeOffset UpdatedAtUtc { get; init; }

        /// <summary>
        /// Optional metadata for future priority, routing, dashboard,
        /// Kubernetes, or shared queue labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets the logical control-plane identifier that owns this shared run record.
        /// </summary>
        public string? ControlPlaneId { get; init; }
    }
}