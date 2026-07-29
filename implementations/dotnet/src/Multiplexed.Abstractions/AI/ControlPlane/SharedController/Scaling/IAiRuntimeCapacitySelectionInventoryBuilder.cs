namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Builds typed hierarchical capacity-selection candidates from the current
    /// distributed runtime capacity inventory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder reuses the existing runtime capacity store, tenant visibility
    /// evaluator, Runtime Pool capacity-safety authority, and admission reservation
    /// store. It does not introduce a parallel capacity or reservation registry.
    /// </para>
    /// <para>
    /// Inventory projection reads current reservations but remains mutation-free. It
    /// does not reserve a run slot or create
    /// process, Pod, or node capacity.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeCapacitySelectionInventoryBuilder
    {
        /// <summary>
        /// Builds deterministic runtime-level selection candidates for one existing
        /// provider scale-out request.
        /// </summary>
        /// <param name="request">The provider-level scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The projected runtime capacity candidates.</returns>
        Task<IReadOnlyList<AiRuntimeCapacitySelectionCandidate>> BuildAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);
    }
}
