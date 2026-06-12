namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines query filters used to list runtime scale-out requests.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestQuery
    {
        /// <summary>
        /// Gets or sets the logical control-plane identifier to query.
        /// </summary>
        public string? ControlPlaneId { get; set; }

        /// <summary>
        /// Gets or sets the tenant identifier to filter by.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the pipeline key to filter by.
        /// </summary>
        public string? PipelineKey { get; set; }

        /// <summary>
        /// Gets or sets the shared run identifier to filter by.
        /// </summary>
        public string? SharedRunId { get; set; }

        /// <summary>
        /// Gets or sets the statuses to include.
        /// </summary>
        public ISet<AiRuntimeScaleOutRequestStatus> Statuses { get; set; } = new HashSet<AiRuntimeScaleOutRequestStatus>();

        /// <summary>
        /// Gets or sets the maximum number of records to return.
        /// </summary>
        public int MaxResults { get; set; } = 100;

        /// <summary>
        /// Gets or sets a value indicating whether expired records should be included.
        /// </summary>
        public bool IncludeExpired { get; set; }

        /// <summary>
        /// Gets or sets the lower UTC creation time bound.
        /// </summary>
        public DateTimeOffset? CreatedAfterUtc { get; set; }

        /// <summary>
        /// Gets or sets the upper UTC creation time bound.
        /// </summary>
        public DateTimeOffset? CreatedBeforeUtc { get; set; }
    }
}