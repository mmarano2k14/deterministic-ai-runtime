using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents a persisted runtime scale-out request created by the control plane.
    /// </summary>
    /// <remarks>
    /// A scale-out request is operational coordination state. It is created when
    /// admission decides that additional runtime capacity is required. The request
    /// can then be observed by MCP tools, diagnostics, or an external scaler adapter.
    /// </remarks>
    public sealed class AiRuntimeScaleOutRequestRecord
    {
        /// <summary>
        /// Gets or sets the unique scale-out request identifier.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logical control-plane identifier that owns this request.
        /// </summary>
        public string ControlPlaneId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the shared run identifier that triggered the scale-out request.
        /// </summary>
        public string SharedRunId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the tenant identifier associated with the request, when available.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the tenant group identifier associated with the request, when available.
        /// </summary>
        public string? TenantGroupId { get; set; }

        /// <summary>
        /// Gets or sets the pipeline key associated with the request, when available.
        /// </summary>
        public string? PipelineKey { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance isolation mode resolved for the tenant.
        /// </summary>
        public AiRuntimeInstanceIsolationMode IsolationMode { get; set; } =
            AiRuntimeInstanceIsolationMode.Shared;

        /// <summary>
        /// Gets or sets a value indicating whether dedicated runtime capacity
        /// should be preferred for the tenant.
        /// </summary>
        public bool PreferDedicatedCapacity { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether shared runtime capacity may be used
        /// when dedicated tenant capacity is not available.
        /// </summary>
        public bool AllowSharedFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of runtime instances allowed for the tenant.
        /// </summary>
        public int? MaxRuntimeInstances { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance identifier prefix that should be used
        /// when creating tenant-specific runtime instances.
        /// </summary>
        public string? RuntimeInstanceIdPrefix { get; set; }

        /// <summary>
        /// Gets or sets the worker count to use for each runtime instance created
        /// for this scale-out request.
        /// </summary>
        public int? WorkerCountPerInstance { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of concurrent runs allowed per runtime instance
        /// created for this scale-out request.
        /// </summary>
        public int? MaxConcurrentRunsPerInstance { get; set; }

        /// <summary>
        /// Gets or sets the local queue capacity to use for runtime instances created
        /// for this scale-out request.
        /// </summary>
        public int? LocalQueueCapacity { get; set; }

        /// <summary>
        /// Gets or sets the current lifecycle status of the scale-out request.
        /// </summary>
        public AiRuntimeScaleOutRequestStatus Status { get; set; } =
            AiRuntimeScaleOutRequestStatus.Pending;

        /// <summary>
        /// Gets or sets the reason why scale-out was requested.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of runtime instances visible at the time of the request.
        /// </summary>
        public int VisibleInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the number of runtime instances available at the time of the request.
        /// </summary>
        public int AvailableInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the current runtime instance count known by admission.
        /// </summary>
        public int CurrentInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the optional maximum runtime instance count allowed by policy.
        /// </summary>
        public int? MaxInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the requested target runtime instance count.
        /// </summary>
        public int RequestedTargetInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets an optional provider hint for a future scaler adapter.
        /// </summary>
        /// <remarks>
        /// Examples include <c>kubernetes</c>, <c>http</c>, or <c>redis-command-queue</c>.
        /// The core runtime must not depend on provider-specific infrastructure here.
        /// </remarks>
        public string? ProviderHint { get; set; }

        /// <summary>
        /// Gets or sets the logical actor that requested the scale-out operation.
        /// </summary>
        public string? RequestedBy { get; set; }

        /// <summary>
        /// Gets or sets the logical source that created the scale-out request.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets the correlation identifier used for diagnostics and tracing.
        /// </summary>
        public string? CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the UTC time when the request was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets the UTC time when the request was first observed.
        /// </summary>
        public DateTimeOffset? ObservedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time when the request was fulfilled.
        /// </summary>
        public DateTimeOffset? FulfilledAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time when the request was rejected.
        /// </summary>
        public DateTimeOffset? RejectedAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time when the request expired.
        /// </summary>
        public DateTimeOffset? ExpiredAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time when the request was cancelled.
        /// </summary>
        public DateTimeOffset? CancelledAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the UTC time after which the request should be considered expired.
        /// </summary>
        public DateTimeOffset? ExpiresAtUtc { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance identifier that fulfilled the request, when available.
        /// </summary>
        public string? FulfilledRuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets the actor or component that observed the request.
        /// </summary>
        public string? ObservedBy { get; set; }

        /// <summary>
        /// Gets or sets the actor or component that fulfilled the request.
        /// </summary>
        public string? FulfilledBy { get; set; }

        /// <summary>
        /// Gets or sets the actor or component that rejected the request.
        /// </summary>
        public string? RejectedBy { get; set; }

        /// <summary>
        /// Gets or sets the reason supplied when the request was rejected.
        /// </summary>
        public string? RejectionReason { get; set; }

        /// <summary>
        /// Gets or sets optional metadata associated with the request.
        /// </summary>
        public IDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}