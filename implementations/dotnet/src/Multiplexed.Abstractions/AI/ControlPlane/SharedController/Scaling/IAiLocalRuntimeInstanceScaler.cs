using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Defines a local runtime instance scaler capable of creating or exposing
    /// additional local runtime instance capacity.
    /// </summary>
    /// <remarks>
    /// This abstraction owns the local pool lifecycle used by both startup pooling
    /// and dynamic local scale-out.
    ///
    /// It does not replace the shared queue, DAG engine, or local runtime queues.
    /// It only ensures that enough local runtime instance hosts exist for the
    /// requested scale-out target.
    /// </remarks>
    public interface IAiLocalRuntimeInstanceScaler : IAsyncDisposable
    {
        /// <summary>
        /// Gets the number of active local runtime instance hosts currently managed by the scaler.
        /// </summary>
        int ActiveInstanceCount { get; }

        /// <summary>
        /// Ensures that local runtime capacity exists for the requested scale-out target.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        Task<AiRuntimeScaleOutProviderResult> EnsureCapacityAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops all local runtime instances managed by this scaler.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task StopAsync(
            CancellationToken cancellationToken = default);
    }
}