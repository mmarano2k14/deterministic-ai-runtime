namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Coordinates hierarchical capacity selection, existing runtime reservation,
    /// bounded process creation, and Runtime Pool Pod creation.
    /// </summary>
    /// <remarks>
    /// This coordinator executes the
    /// <see cref="AiRuntimeCapacitySelectionLevel.ExistingPoolPodProcessCreation" />
    /// mutation introduced by Step 7D and the
    /// <see cref="AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation" />
    /// mutation introduced by Step 7E. External node capacity and backpressure remain
    /// separate hierarchy outcomes.
    /// </remarks>
    public interface IAiRuntimeHierarchicalCapacityExecutionCoordinator
    {
        /// <summary>
        /// Selects and reserves capacity, then executes the selected process or Pod
        /// creation action when required.
        /// </summary>
        /// <param name="request">The provider-level capacity request.</param>
        /// <param name="runCount">The number of runtime run slots to reserve.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The hierarchical execution result.</returns>
        Task<AiRuntimeHierarchicalCapacityExecutionResult>
            SelectReserveAndExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                int runCount = 1,
                CancellationToken cancellationToken = default);
    }
}
