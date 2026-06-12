using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Requeues a shared run after a scale-out request has been fulfilled.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Keeps the scale-out watcher focused on scale-out request processing.
    /// - Allows the normal shared queue pump to redispatch the run after capacity exists.
    /// - Avoids dispatching directly from the watcher.
    /// </remarks>
    public interface IAiScaleOutFulfilledRunRequeueService
    {
        /// <summary>
        /// Requeues the shared run linked to a fulfilled scale-out request.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="runtimeInstanceId">The runtime instance created or selected by scale-out.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task representing the operation.</returns>
        Task RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}