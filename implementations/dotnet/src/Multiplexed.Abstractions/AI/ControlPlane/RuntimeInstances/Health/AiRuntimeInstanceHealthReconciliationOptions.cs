namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Options controlling runtime instance health reconciliation.
    /// </summary>
    public sealed class AiRuntimeInstanceHealthReconciliationOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether health reconciliation is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the heartbeat age after which a runtime instance is considered stale.
        /// </summary>
        public TimeSpan StaleHeartbeatThreshold { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets a value indicating whether stale runtime instances should be marked unhealthy.
        /// </summary>
        public bool MarkStaleRuntimeUnhealthy { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether busy runtime instances are included in reconciliation.
        /// </summary>
        public bool IncludeBusyRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether ready runtime instances are included in reconciliation.
        /// </summary>
        public bool IncludeReadyRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether paused runtime instances are ignored.
        /// </summary>
        public bool IgnorePausedRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether stopped runtime instances are ignored.
        /// </summary>
        public bool IgnoreStoppedRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether draining runtime instances are ignored.
        /// </summary>
        public bool IgnoreDrainingRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether reconciliation should only report decisions without applying changes.
        /// </summary>
        public bool DryRun { get; set; }
    }
}