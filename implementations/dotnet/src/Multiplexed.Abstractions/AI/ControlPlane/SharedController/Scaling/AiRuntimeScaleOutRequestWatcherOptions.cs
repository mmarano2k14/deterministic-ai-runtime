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
        /// Gets or sets the maximum number of pending requests processed per cycle.
        /// </summary>
        public int MaxRequestsPerCycle { get; set; } = 10;

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