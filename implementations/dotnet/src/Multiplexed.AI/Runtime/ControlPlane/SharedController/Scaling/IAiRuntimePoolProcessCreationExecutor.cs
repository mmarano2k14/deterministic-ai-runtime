using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Creates bounded child-process capacity inside the exact local Runtime Pool host
    /// selected by the hierarchical capacity selector.
    /// </summary>
    public interface IAiRuntimePoolProcessCreationExecutor
    {
        /// <summary>
        /// Applies one idempotent process-capacity request to the selected host.
        /// </summary>
        /// <param name="request">The provider-level scale-out request.</param>
        /// <param name="candidate">The selected existing-host candidate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact process-creation result.</returns>
        Task<AiRuntimePoolProcessCreationResult> ExecuteAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            CancellationToken cancellationToken = default);
    }
}
