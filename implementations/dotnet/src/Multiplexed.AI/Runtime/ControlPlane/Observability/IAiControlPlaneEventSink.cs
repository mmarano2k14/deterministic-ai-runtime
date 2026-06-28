using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.Observability
{
    /// <summary>
    /// Consumes structured control-plane events for one observability backend.
    /// </summary>
    public interface IAiControlPlaneEventSink
    {
        /// <summary>
        /// Records a structured control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event to record.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the event has been recorded.</returns>
        Task RecordAsync(
            AiControlPlaneEvent controlPlaneEvent,
            CancellationToken cancellationToken = default);
    }
}