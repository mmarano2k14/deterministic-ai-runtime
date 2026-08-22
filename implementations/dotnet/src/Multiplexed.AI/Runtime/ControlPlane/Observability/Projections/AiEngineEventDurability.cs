namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Classifies the durability semantics of one canonical engine event.
    /// </summary>
    /// <remarks>
    /// This classification describes the semantic fact itself. Projection-specific delivery
    /// requirements are declared separately by <see cref="AiEngineEventProjectionDescriptor"/>.
    /// </remarks>
    public enum AiEngineEventDurability
    {
        /// <summary>
        /// The event is an observational fact that does not itself require durable evidence.
        /// </summary>
        TransientObservation = 0,

        /// <summary>
        /// The event represents a durable execution or runtime lifecycle fact.
        /// </summary>
        DurableLifecycleFact = 1,

        /// <summary>
        /// The event represents a durable recovery fact.
        /// </summary>
        DurableRecoveryFact = 2,

        /// <summary>
        /// The event represents a durable decision or decision-adjacent fact.
        /// </summary>
        DurableDecisionFact = 3,

        /// <summary>
        /// The event is owned by the append-only runtime lifecycle journal contract.
        /// </summary>
        RuntimeJournalFact = 4
    }
}
