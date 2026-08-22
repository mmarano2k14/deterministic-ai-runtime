namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Defines the delivery requirement for one canonical event projection.
    /// </summary>
    public enum AiEngineEventProjectionRequirement
    {
        /// <summary>
        /// The projection does not receive the event.
        /// </summary>
        None = 0,

        /// <summary>
        /// The projection is observational and its failure must not fail the semantic event emission.
        /// </summary>
        BestEffort = 1,

        /// <summary>
        /// The projection is durable and may be retried idempotently from durable evidence.
        /// </summary>
        ReplayableDurable = 2,

        /// <summary>
        /// The semantic event emission is not successful unless the projection completes according
        /// to the existing durable implementation contract.
        /// </summary>
        RequiredDurable = 3
    }
}
