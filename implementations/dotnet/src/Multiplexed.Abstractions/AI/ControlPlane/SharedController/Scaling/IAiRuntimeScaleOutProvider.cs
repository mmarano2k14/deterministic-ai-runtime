using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines a runtime instance provider capability capable of fulfilling
    /// runtime scale-out requests.
    /// </summary>
    /// <remarks>
    /// This is not a separate provider routing model.
    /// Implementations are resolved through the existing runtime instance provider
    /// system and selected by the existing provider metadata, such as
    /// <c>provider.name</c>.
    ///
    /// Implementations may scale local runtime pools, call an HTTP runtime host,
    /// or interact with Kubernetes.
    ///
    /// This abstraction does not read or write the scale-out request store directly.
    /// Store lifecycle transitions are owned by the watcher or orchestrator.
    /// </remarks>
    public interface IAiRuntimeScaleOutProvider :
        IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Requests runtime scale-out.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);
    }
}