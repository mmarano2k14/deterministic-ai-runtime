namespace Multiplexed.Abstractions.AI.ControlPlane.Discovery
{
    /// <summary>
    /// Defines a store used to publish and resolve control-plane discovery descriptors.
    /// </summary>
    public interface IAiControlPlaneDiscoveryStore
    {
        /// <summary>
        /// Publishes or refreshes a control-plane discovery descriptor.
        /// </summary>
        /// <param name="descriptor">The descriptor to publish.</param>
        /// <param name="ttl">The optional time-to-live to apply to the discovery entry.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task PublishAsync(
            AiControlPlaneDiscoveryDescriptor descriptor,
            TimeSpan? ttl,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a control-plane discovery descriptor by Redis discovery key.
        /// </summary>
        /// <param name="redisDiscoveryKey">The Redis discovery key.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The discovery descriptor when found; otherwise, <c>null</c>.</returns>
        Task<AiControlPlaneDiscoveryDescriptor?> GetAsync(
            string redisDiscoveryKey,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a control-plane discovery descriptor by Redis discovery key.
        /// </summary>
        /// <param name="redisDiscoveryKey">The Redis discovery key.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task DeleteAsync(
            string redisDiscoveryKey,
            CancellationToken cancellationToken = default);
    }
}