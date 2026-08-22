using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Resolves previously persisted canonical engine-event evidence for deterministic lifecycle waits.
    /// </summary>
    /// <remarks>
    /// Implementations are read-only. They must never drive execution decisions or mutate durable state.
    /// </remarks>
    public interface IAiDeterministicLifecycleEvidenceReader
    {
        /// <summary>
        /// Attempts to rehydrate one canonical event matching the requested durable evidence.
        /// </summary>
        /// <param name="criteria">The canonical event and identity filters.</param>
        /// <param name="cancellationToken">A token used to cancel the read.</param>
        /// <returns>The matching canonical event when durable evidence exists; otherwise, <c>null</c>.</returns>
        Task<AiControlPlaneEvent?> FindAsync(
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken = default);
    }
}
