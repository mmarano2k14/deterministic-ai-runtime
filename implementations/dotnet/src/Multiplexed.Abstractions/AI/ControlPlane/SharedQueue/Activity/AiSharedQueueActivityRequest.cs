namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity
{
    /// <summary>
    /// Represents a request for recent shared queue activity.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Allows MCP, HTTP APIs, dashboards, CLIs, and Kubernetes diagnostics
    ///   to inspect recent shared run activity.
    /// - Complements active shared queue inspection by showing runs that may
    ///   already have been claimed, dispatched, completed, failed, or cancelled.
    ///
    /// IMPORTANT:
    /// - This request does not read the transient active queue only.
    /// - Implementations should typically read from the shared run store.
    /// - This is intended for visibility and diagnostics, not scheduling.
    /// </remarks>
    public sealed class AiSharedQueueActivityRequest
    {
        /// <summary>
        /// Gets or sets the maximum number of activity records to return.
        /// </summary>
        public int MaxResults { get; set; } = 100;

        /// <summary>
        /// Gets or sets an optional pipeline key filter.
        /// </summary>
        public string? PipelineKey { get; set; }

        /// <summary>
        /// Gets or sets an optional tenant identifier filter.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether completed runs should be included.
        /// </summary>
        public bool IncludeCompleted { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether failed runs should be included.
        /// </summary>
        public bool IncludeFailed { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether cancelled runs should be included.
        /// </summary>
        public bool IncludeCancelled { get; set; } = true;
    }
}