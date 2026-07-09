using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Requeues shared runs after scale-out fulfillment.
    /// </summary>
    public interface IAiScaleOutFulfilledRunRequeueService
    {
        /// <summary>
        /// Requeues shared runs after scale-out fulfillment.
        /// </summary>
        /// <param name="request">The fulfilled scale-out request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id created by scale-out.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The requeue result.</returns>
        Task<AiScaleOutFulfilledRunRequeueResult> RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}