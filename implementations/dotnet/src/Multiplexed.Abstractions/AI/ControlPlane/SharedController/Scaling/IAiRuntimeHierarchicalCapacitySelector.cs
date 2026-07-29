namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Selects the first safe capacity candidate from the ordered runtime capacity
    /// hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The selector reuses <see cref="AiRuntimeScaleOutProviderRequest" /> so tenant,
    /// isolation, provider, and execution-context authority remain aligned with the
    /// existing scale-out pipeline.
    /// </para>
    /// <para>
    /// Selection is read-only. Atomic runtime-slot reservation and bounded process,
    /// Pod, or node scale-out remain the responsibility of their existing or later
    /// execution components.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeHierarchicalCapacitySelector
    {
        /// <summary>
        /// Selects one capacity candidate or returns a backpressure decision.
        /// </summary>
        /// <param name="request">The existing provider-level scale-out request.</param>
        /// <param name="candidates">The typed capacity candidates.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The deterministic capacity selection decision.</returns>
        Task<AiRuntimeCapacitySelectionDecision> SelectAsync(
            AiRuntimeScaleOutProviderRequest request,
            IReadOnlyList<AiRuntimeCapacitySelectionCandidate> candidates,
            CancellationToken cancellationToken = default);
    }
}
