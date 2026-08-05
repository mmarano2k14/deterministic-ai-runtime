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

        /// <summary>
        /// Gets or sets the minimum continuous absence period required before a recoverable run
        /// whose runtime instance is missing from the registry is treated as orphaned.
        /// </summary>
        /// <remarks>
        /// A single missing Redis registry read is not authoritative proof that a runtime process
        /// or Kubernetes Pod has failed. The confirmation period prevents temporary registry lease
        /// expiry, Redis latency, or heartbeat starvation under load from triggering false recovery.
        /// Explicit unhealthy, stopped, or draining runtime states are still processed immediately.
        /// </remarks>
        public TimeSpan OrphanedRuntimeInstanceConfirmationPeriod { get; set; } =
            TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets a value indicating whether runtime execution recovery should try to resume
        /// the existing durable DAG execution instead of creating a new recovered execution.
        /// </summary>
        /// <remarks>
        /// The default is <see langword="false"/> to preserve the existing recovery behavior:
        /// recover shared run ownership, requeue the shared run, redispatch to healthy capacity,
        /// and create a new recovered execution.
        /// </remarks>
        public bool EnableDagExecutionResume { get; set; } = false;
    }
}