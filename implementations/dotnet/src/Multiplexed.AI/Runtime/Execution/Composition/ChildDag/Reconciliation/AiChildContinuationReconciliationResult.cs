namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Reconciliation
{
    /// <summary>
    /// Describes one durable child-continuation reconciliation iteration.
    /// </summary>
    public sealed class AiChildContinuationReconciliationResult
    {
        /// <summary>
        /// Gets the number of incomplete child relations inspected for terminal child state.
        /// </summary>
        public int IncompleteRelationCount { get; init; }

        /// <summary>
        /// Gets the number of child executions projected into authoritative completed relations.
        /// </summary>
        public int CompletedRelationCount { get; init; }

        /// <summary>
        /// Gets the number of pending or scheduled continuation relations inspected.
        /// </summary>
        public int ContinuationCandidateCount { get; init; }

        /// <summary>
        /// Gets the number of defensive park-consistency candidates inspected.
        /// </summary>
        public int ParkConsistencyCandidateCount { get; init; }

        /// <summary>
        /// Gets the number of defensive parent park re-drives that were enqueued.
        /// </summary>
        public int ParkRepairEnqueueCount { get; init; }
    }
}
