namespace Multiplexed.Abstractions.AI.ControlPlane.Discovery
{
    /// <summary>
    /// Resolves the logical control-plane identifier used to isolate shared runtime state.
    /// </summary>
    public interface IAiControlPlaneIdResolver
    {
        /// <summary>
        /// Resolves the logical control-plane identifier for the current host.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved control-plane identifier.</returns>
        Task<string> ResolveAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves the logical control-plane identifier from explicit request metadata and fallback sources.
        /// </summary>
        /// <param name="request">The control-plane identifier resolution request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        Task<string> ResolveAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves the logical control-plane identifier and creates canonical metadata aliases for it.
        /// </summary>
        /// <param name="request">The control-plane identifier resolution request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved control-plane metadata.</returns>
        Task<IReadOnlyDictionary<string, string>> ResolveMetadataAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default);
    }
}