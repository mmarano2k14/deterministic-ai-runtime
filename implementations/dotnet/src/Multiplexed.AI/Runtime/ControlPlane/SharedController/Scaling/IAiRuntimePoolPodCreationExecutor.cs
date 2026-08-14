using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Executes one deterministic Kubernetes Runtime Pool Pod creation decision.
    /// </summary>
    public interface IAiRuntimePoolPodCreationExecutor
    {
        /// <summary>
        /// Creates and converges one new Runtime Pool Pod for the exact selected pool.
        /// </summary>
        /// <param name="request">The provider-level scale-out request.</param>
        /// <param name="candidate">The selected Pod-creation candidate.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact Pod creation result.</returns>
        Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeCapacitySelectionCandidate candidate,
            CancellationToken cancellationToken = default);
    }
}
