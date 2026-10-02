namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>Keyset cursor for continuation reconciliation; it is not durable authority.</summary>
    public sealed record AiDurableInvocationContinuationCursor(DateTimeOffset UpdatedAtUtc, string OperationId);

    /// <summary>
    /// Optional bounded continuation-read capability. Ordering is updated-at then ordinal
    /// operation ID; the cursor is exclusive and may be discarded after process restart.
    /// Durable continuation state and CAS transitions remain authoritative.
    /// </summary>
    public interface IAiDurableInvocationContinuationPageStore
    {
        Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationPageAsync(
            AiDurableInvocationScope scope, int maxCount,
            AiDurableInvocationContinuationCursor? after = null,
            CancellationToken cancellationToken = default);
    }
}
