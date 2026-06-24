namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Represents the result of a runtime execution recovery reconciliation pass.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryReconciliationResult
    {
        /// <summary>
        /// Gets the number of runtime instances scanned.
        /// </summary>
        public int ScannedRuntimeInstanceCount { get; init; }

        /// <summary>
        /// Gets the number of unfinished runtime runs discovered.
        /// </summary>
        public int DiscoveredUnfinishedRunCount { get; init; }

        /// <summary>
        /// Gets the number of recovery mutations applied.
        /// </summary>
        public int RecoveredRunCount { get; init; }

        /// <summary>
        /// Gets the number of runtime instances ignored.
        /// </summary>
        public int IgnoredRuntimeInstanceCount { get; init; }

        /// <summary>
        /// Gets the recovery decisions produced during reconciliation.
        /// </summary>
        public IReadOnlyList<AiRuntimeExecutionRecoveryDecision> Decisions { get; init; } =
            Array.Empty<AiRuntimeExecutionRecoveryDecision>();
    }
}