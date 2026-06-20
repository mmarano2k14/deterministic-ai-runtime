using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents a provider-level request to create or expose additional runtime capacity.
    /// </summary>
    /// <remarks>
    /// This request is produced from a persisted runtime scale-out request record.
    /// It contains only the information a provider needs to decide whether and how
    /// to fulfill scale-out.
    /// </remarks>
    public sealed class AiRuntimeScaleOutProviderRequest
    {
        /// <summary>
        /// Gets or sets the scale-out request identifier.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the logical control-plane identifier.
        /// </summary>
        public string ControlPlaneId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the shared run identifier that triggered scale-out.
        /// </summary>
        public string SharedRunId { get; set; } = string.Empty;

        /// <summary>
        /// Gets the execution context snapshot that caused the scale-out request.
        /// </summary>
        /// <remarks>
        /// The execution context snapshot is the durable authority for tenant/runtime isolation.
        /// Scale-out providers and runtime host managers must not derive tenant ownership from metadata
        /// when this snapshot is available.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets or sets the tenant identifier associated with the request.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the tenant group identifier associated with the request.
        /// </summary>
        public string? TenantGroupId { get; set; }

        /// <summary>
        /// Gets or sets the pipeline key associated with the request.
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
        /// Gets or sets the number of runtime instances visible when scale-out was requested.
        /// </summary>
        public int VisibleInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the number of runtime instances available when scale-out was requested.
        /// </summary>
        public int AvailableInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the current runtime instance count.
        /// </summary>
        public int CurrentInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the maximum runtime instance count allowed by policy.
        /// </summary>
        public int? MaxInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets the requested target runtime instance count.
        /// </summary>
        public int RequestedTargetInstanceCount { get; set; }

        /// <summary>
        /// Gets or sets an optional provider hint.
        /// </summary>
        public string? ProviderHint { get; set; }

        /// <summary>
        /// Gets or sets the correlation identifier.
        /// </summary>
        public string? CorrelationId { get; set; }

        /// <summary>
        /// Gets or sets the actor that requested the original run.
        /// </summary>
        public string? RequestedBy { get; set; }

        /// <summary>
        /// Gets or sets the source that requested the original run.
        /// </summary>
        public string? Source { get; set; }

        /// <summary>
        /// Gets or sets the reason for the scale-out request.
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// Gets or sets provider metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();
    }
}