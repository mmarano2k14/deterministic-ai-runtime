namespace Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations
{
    /// <summary>
    /// Represents the durable parent continuation lifecycle after a child DAG reaches an authoritative outcome.
    /// </summary>
    public enum AiChildContinuationStatus
    {
        /// <summary>
        /// No parent continuation is currently required.
        /// </summary>
        None = 0,

        /// <summary>
        /// Child completion requires a parent continuation but scheduling has not yet been durably claimed.
        /// </summary>
        Pending = 1,

        /// <summary>
        /// Parent continuation scheduling has been durably recorded and may be safely re-enqueued.
        /// </summary>
        Scheduled = 2,

        /// <summary>
        /// The parent has durably demonstrated progress after the scheduled continuation.
        /// </summary>
        Resumed = 3,

        /// <summary>
        /// The continuation was durably suppressed because the parent became terminal before it could be consumed.
        /// </summary>
        Suppressed = 4
    }
}
