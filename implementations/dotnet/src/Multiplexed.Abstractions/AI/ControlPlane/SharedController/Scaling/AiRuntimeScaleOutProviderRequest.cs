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
        /// Gets or sets the tenant identifier associated with the request.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the pipeline key associated with the request.
        /// </summary>
        public string? PipelineKey { get; set; }

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