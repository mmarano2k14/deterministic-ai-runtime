namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Represents reusable control-plane causal-chain ledger proof counts.
    /// </summary>
    /// <param name="ExpectedRecoveredWorkCount">The expected recovered work count.</param>
    /// <param name="ActualRecoveredWorkCount">The actual recovered work count.</param>
    /// <param name="ScaleOutRequestPersistedCount">The scale-out request persisted count.</param>
    /// <param name="ScaleOutWatcherObservedCount">The scale-out watcher observed count.</param>
    /// <param name="ProviderSelectedCount">The provider selected count.</param>
    /// <param name="RuntimeHostCreatedCount">The effective runtime host creation count.</param>
    /// <param name="ProcessRuntimeHostStartedCount">The process runtime host creation count.</param>
    /// <param name="RuntimeCapacityVisibleCount">The runtime capacity visibility count.</param>
    /// <param name="RuntimeRegistryVisibleCount">The runtime registry visibility count.</param>
    /// <param name="FailedRuntimeMarkedUnhealthyCount">The failed runtime unhealthy marker ledger count.</param>
    /// <param name="DirectFailedRuntimeUnsafeValidated">Whether failed runtime unsafe state was validated directly from registry state.</param>
    /// <param name="ExecutionRecoveryReconciledCount">The execution recovery reconcile count.</param>
    /// <param name="RecoveredWorkRedispatchedCount">The recovered work redispatch count.</param>
    public sealed record ProductionControlPlaneLedgerCausalChainProofResult(
        int ExpectedRecoveredWorkCount,
        int ActualRecoveredWorkCount,
        int ScaleOutRequestPersistedCount,
        int ScaleOutWatcherObservedCount,
        int ProviderSelectedCount,
        int RuntimeHostCreatedCount,
        int ProcessRuntimeHostStartedCount,
        int RuntimeCapacityVisibleCount,
        int RuntimeRegistryVisibleCount,
        int FailedRuntimeMarkedUnhealthyCount,
        bool DirectFailedRuntimeUnsafeValidated,
        int ExecutionRecoveryReconciledCount,
        int RecoveredWorkRedispatchedCount)
    {
        /// <summary>
        /// Gets a value indicating whether failed runtime unsafe state was validated by ledger marker or direct registry state.
        /// </summary>
        public bool FailedRuntimeUnsafeValidated =>
            this.FailedRuntimeMarkedUnhealthyCount > 0 ||
            this.DirectFailedRuntimeUnsafeValidated;

        /// <summary>
        /// Gets a value indicating whether the full causal chain is validated.
        /// </summary>
        public bool IsValidated =>
            this.ExpectedRecoveredWorkCount == this.ActualRecoveredWorkCount &&
            this.ScaleOutRequestPersistedCount > 0 &&
            this.ScaleOutWatcherObservedCount > 0 &&
            this.ProviderSelectedCount > 0 &&
            this.RuntimeHostCreatedCount > 0 &&
            this.ProcessRuntimeHostStartedCount > 0 &&
            this.RuntimeCapacityVisibleCount > 0 &&
            this.RuntimeRegistryVisibleCount > 0 &&
            this.FailedRuntimeUnsafeValidated &&
            this.ExecutionRecoveryReconciledCount > 0 &&
            this.RecoveredWorkRedispatchedCount > 0;
    }
}