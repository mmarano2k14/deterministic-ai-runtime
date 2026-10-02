namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Describes why an invocation CAS did or did not apply. Persistence remains authoritative;
    /// callers use the classification only to decide whether a reload/retry is meaningful.
    /// </summary>
    public enum AiDurableInvocationCasOutcomeKind
    {
        Applied = 0,
        RevisionConflict = 1,
        AuthorityPredicateRejected = 2
    }
}
