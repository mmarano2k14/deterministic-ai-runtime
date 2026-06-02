namespace Multiplexed.Abstractions.AI.ControlPlane.Replay
{
    /// <summary>
    /// Represents an adapter-neutral replay control-plane request.
    ///
    /// This request can be used later by HTTP API, MCP, CLI, dashboard,
    /// or Kubernetes control-plane adapters without coupling them to
    /// ASP.NET, MCP, Kubernetes, or replay engine internals.
    /// </summary>
    public sealed class AiReplayControlRequest
    {
        /// <summary>
        /// Durable shared DAG execution identifier.
        /// </summary>
        public required string ExecutionId { get; init; }

        /// <summary>
        /// Requested replay control-plane operation.
        /// </summary>
        public required AiReplayOperation Operation { get; set; }

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
        /// Optional reason explaining why the replay control operation was requested.
        /// Useful for audit, diagnostics, and operator history.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Optional runtime instance id when the request is issued from
        /// a runtime instance or Kubernetes pod.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Indicates whether replay diagnostics should be included when available.
        /// </summary>
        public bool IncludeDiagnostics { get; init; } = true;

        /// <summary>
        /// Indicates whether the replay report should be included when available.
        /// </summary>
        public bool IncludeReport { get; init; } = true;

        /// <summary>
        /// Indicates whether decision ledger data should be included when available.
        /// </summary>
        public bool IncludeLedger { get; init; } = true;

        /// <summary>
        /// Indicates whether trace timeline data should be included when available.
        /// </summary>
        public bool IncludeTimeline { get; init; } = true;

        /// <summary>
        /// Indicates whether deterministic mismatches should be treated as
        /// strict controller-level failures.
        ///
        /// The replay engine remains the source of truth. This flag only controls
        /// how the control-plane facade interprets the replay result.
        /// </summary>
        public bool StrictDeterminism { get; init; } = true;
    }
}