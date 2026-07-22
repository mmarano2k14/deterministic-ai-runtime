namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines options for the runtime scale-out request watcher.
    /// </summary>
    /// <remarks>
    /// The watcher observes pending scale-out requests from the configured store
    /// and forwards them to a runtime scale-out provider.
    /// </remarks>
    public sealed class AiRuntimeScaleOutRequestWatcherOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the scale-out request watcher is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the logical control-plane identifier watched by this process.
        /// </summary>
        public string? ControlPlaneId { get; set; }

        /// <summary>
        /// Gets or sets the watcher identifier used for lifecycle transitions.
        /// </summary>
        public string WatcherId { get; set; } = "scale-out-request-watcher";

        /// <summary>
        /// Gets or sets the watcher polling interval.
        /// </summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the maximum number of pending requests tracked by one watcher.
        /// </summary>
        /// <remarks>
        /// When process-wide coordination is enabled, tracked requests are lightweight
        /// coordinator entries. Their observer, store, provider, and terminal workflow
        /// does not start until global admission is granted.
        /// </remarks>
        public int MaxRequestsPerCycle { get; set; } = 10;

        /// <summary>
        /// Gets or sets a value indicating whether the complete watcher workflow uses
        /// process-wide coordination.
        /// </summary>
        public bool EnableProcessWideRequestProcessingCoordination { get; set; }

        /// <summary>
        /// Gets or sets the process-wide request-processing coordinator key.
        /// </summary>
        public string RequestProcessingCoordinationKey { get; set; } =
            "runtime-scale-out-request-processing";

        /// <summary>
        /// Gets or sets the maximum number of complete scale-out request-processing
        /// workflows active across all control planes sharing
        /// <see cref="RequestProcessingCoordinationKey" /> in the current process.
        /// </summary>
        public int MaxConcurrentRequestProcessingWorkflows { get; set; } = 6;

        /// <summary>
        /// Gets or sets the maximum number of complete request-processing workflows
        /// active for one control plane.
        /// </summary>
        public int MaxConcurrentRequestProcessingWorkflowsPerControlPlane { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum consecutive recovery dispatches while normal
        /// scale-out work is also waiting.
        /// </summary>
        public int RecoveryDispatchBurstLimit { get; set; } = 3;

        /// <summary>
        /// Gets or sets a value indicating whether provider failures should reject the scale-out request.
        /// </summary>
        public bool RejectOnProviderFailure { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether pending requests without a control-plane id should be ignored.
        /// </summary>
        public bool IgnoreWhenControlPlaneIdMissing { get; set; } = true;
    }
}
