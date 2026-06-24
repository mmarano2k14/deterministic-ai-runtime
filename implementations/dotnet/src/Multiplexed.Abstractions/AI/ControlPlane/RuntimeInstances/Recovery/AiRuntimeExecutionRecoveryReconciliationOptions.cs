namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Options controlling runtime execution recovery reconciliation.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryReconciliationOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether runtime execution recovery reconciliation is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether recovery should inspect unhealthy runtime instances.
        /// </summary>
        public bool IncludeUnhealthyRuntimeInstances { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether recovery should inspect stopped runtime instances.
        /// </summary>
        public bool IncludeStoppedRuntimeInstances { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether recovery should inspect draining runtime instances.
        /// </summary>
        public bool IncludeDrainingRuntimeInstances { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether unfinished runs should be requeued.
        /// </summary>
        /// <remarks>
        /// Keep this disabled until the recovery transition is validated end-to-end against
        /// shared queue ownership, shared run store ownership, and runtime run execution index state.
        /// </remarks>
        public bool RequeueUnfinishedRuns { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether reconciliation should only report decisions without mutations.
        /// </summary>
        public bool DryRun { get; set; } = true;
    }
}