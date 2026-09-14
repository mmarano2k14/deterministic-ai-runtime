namespace Multiplexed.Abstractions.AI.Invocation.Durable
{
    /// <summary>
    /// Internal persistence boundary. Implementations must enforce identity uniqueness,
    /// immutable preparation, revision CAS and live-lease checks atomically. A worker or
    /// SDK must never receive this interface or direct database access.
    /// </summary>
    public interface IAiDurableInvocationStore
    {
        /// <summary>Reads only the requested tenant/group/control-plane-owned operation.</summary>
        Task<AiDurableInvocationRecord?> GetAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts a validated Prepared record or returns its equivalent durable record.
        /// Conflicting frozen data must throw; an existing terminal record is never reset.
        /// </summary>
        Task<AiDurableInvocationRecord> GetOrCreateAsync(
            AiDurableInvocationRecord prepared, CancellationToken cancellationToken = default);

        /// <summary>
        /// Replaces exactly the expected snapshot with one legal transition. Returns false
        /// on CAS/lease-time rejection; storage failures and ambiguous writes must throw.
        /// </summary>
        Task<bool> TryReplaceAsync(
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists Prepared or expired-Leased records. The time is a readiness hint; the
        /// store must still enforce its authoritative lease clock at the atomic write.
        /// </summary>
        Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchCandidatesAsync(
            AiDurableInvocationScope scope, string executionLanguage, DateTimeOffset nowUtc,
            int maxCount, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists both Pending and Scheduled terminal records. Scheduling never removes a
        /// record from reconciliation merely because a queue accepted a notification.
        /// </summary>
        Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationCandidatesAsync(
            AiDurableInvocationScope scope, int maxCount,
            CancellationToken cancellationToken = default);
    }
}
