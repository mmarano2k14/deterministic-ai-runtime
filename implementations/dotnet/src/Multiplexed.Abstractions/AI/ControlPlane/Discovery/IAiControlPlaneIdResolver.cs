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
        Task<string> ResolveAsync(CancellationToken cancellationToken = default);
    }
}