using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Selects and invokes the runtime scale-out provider capability from the
    /// already registered runtime instance provider system.
    /// </summary>
    /// <remarks>
    /// This selector does not introduce a separate provider routing model.
    /// It reuses the runtime instance provider model and resolves a provider
    /// that supports <see cref="IAiRuntimeScaleOutProvider" />.
    /// </remarks>
    public interface IAiRuntimeScaleOutProviderSelector
    {
        /// <summary>
        /// Requests runtime scale-out using the provider resolved from the request
        /// or from the current runtime instance registration configuration.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);
    }
}