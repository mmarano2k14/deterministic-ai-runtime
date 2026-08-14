namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Coordinates deterministic hierarchical capacity selection with bounded atomic
    /// reservation of existing runtime slots.
    /// </summary>
    /// <remarks>
    /// The coordinator reserves only the
    /// <see cref="AiRuntimeCapacitySelectionLevel.CompatibleWarmRuntime" /> and
    /// <see cref="AiRuntimeCapacitySelectionLevel.ExistingPoolRuntimeSlot" /> levels.
    /// Process, Pod, node, and backpressure decisions remain mutation-free for later
    /// hierarchy executors.
    /// </remarks>
    public interface IAiRuntimeHierarchicalCapacityReservationCoordinator
    {
        /// <summary>
        /// Selects the first safe hierarchical capacity action and atomically reserves
        /// an existing runtime slot when required by the selected level.
        /// </summary>
        /// <param name="request">The provider-level capacity request.</param>
        /// <param name="runCount">The number of run slots to reserve.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The final selection and reservation result.</returns>
        Task<AiRuntimeHierarchicalCapacityReservationResult>
            SelectAndReserveAsync(
                AiRuntimeScaleOutProviderRequest request,
                int runCount = 1,
                CancellationToken cancellationToken = default);
    }
}
