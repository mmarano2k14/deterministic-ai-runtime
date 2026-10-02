namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Classified durable invocation CAS result. CurrentRecord is populated when a conflicting
    /// durable snapshot was observed and may be reused by the caller as the next retry candidate.
    /// </summary>
    public sealed record AiDurableInvocationCasOutcome(
        AiDurableInvocationCasOutcomeKind Kind,
        AiDurableInvocationRecord? CurrentRecord = null)
    {
        public static AiDurableInvocationCasOutcome Applied() =>
            new(AiDurableInvocationCasOutcomeKind.Applied);

        public static AiDurableInvocationCasOutcome RevisionConflict(AiDurableInvocationRecord? currentRecord) =>
            new(AiDurableInvocationCasOutcomeKind.RevisionConflict, currentRecord);

        public static AiDurableInvocationCasOutcome AuthorityPredicateRejected() =>
            new(AiDurableInvocationCasOutcomeKind.AuthorityPredicateRejected);
    }
}
