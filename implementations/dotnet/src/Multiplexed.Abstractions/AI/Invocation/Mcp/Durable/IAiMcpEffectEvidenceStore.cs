namespace Multiplexed.Abstractions.AI.Invocation.Mcp.Durable
{
    /// <summary>
    /// Internal persistence boundary for outbound MCP effect evidence. Implementations
    /// enforce immutable intent, tenant scoping and revision CAS atomically. This store
    /// does not invoke tools, retry effects, mutate DAG state or decide reconciliation.
    /// </summary>
    public interface IAiMcpEffectEvidenceStore
    {
        Task<AiMcpEffectEvidenceRecord?> GetAsync(
            AiMcpEffectEvidenceScope scope,
            string effectId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts the Prepared intent or returns the equivalent existing record. The same
        /// logical EffectId with different frozen intent is an integrity conflict.
        /// </summary>
        Task<AiMcpEffectEvidenceRecord> GetOrCreateAsync(
            AiMcpEffectEvidenceRecord prepared,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Replaces exactly the expected revision with one legal evidence transition.
        /// False means the caller lost the CAS; ambiguous storage failures must throw.
        /// </summary>
        Task<bool> TryReplaceAsync(
            AiMcpEffectEvidenceRecord expected,
            AiMcpEffectEvidenceRecord replacement,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists uncertain evidence and dispatches old enough to require reconciliation.
        /// This is discovery only; returned records grant no re-emission authority.
        /// </summary>
        Task<IReadOnlyList<AiMcpEffectEvidenceRecord>> ListReconciliationCandidatesAsync(
            AiMcpEffectEvidenceScope scope,
            DateTimeOffset dispatchStartedBeforeUtc,
            int maxCount,
            CancellationToken cancellationToken = default);
    }
}
