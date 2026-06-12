namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines a provider capable of fulfilling runtime scale-out requests.
    /// </summary>
    /// <remarks>
    /// The provider is responsible for turning an observed scale-out request into
    /// actual runtime capacity.
    ///
    /// Implementations may scale local runtime pools, call an HTTP runtime host,
    /// or interact with Kubernetes.
    ///
    /// This abstraction does not read or write the scale-out request store directly.
    /// Store lifecycle transitions are owned by the watcher or orchestrator.
    /// </remarks>
    public interface IAiRuntimeScaleOutProvider
    {
        /// <summary>
        /// Requests additional runtime capacity.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);
    }
}