namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Optional richer CAS capability for stores that can distinguish stale expected state from
    /// an authoritative server-side predicate rejection. The basic store contract remains valid
    /// for implementations that cannot make that distinction.
    /// </summary>
    public interface IAiDurableInvocationClassifiedCasStore
    {
        Task<AiDurableInvocationCasOutcome> TryReplaceClassifiedAsync(
            AiDurableInvocationRecord expected,
            AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken = default);
    }
}
