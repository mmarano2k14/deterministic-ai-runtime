using System.Collections.Generic;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;

namespace Multiplexed.Abstractions.AI.ControlPlane.Observability
{
    /// <summary>
    /// Provides deterministic waits over canonical engine events observed through the existing Event Manager.
    /// </summary>
    /// <remarks>
    /// Implementations must close the in-process missed-event race by checking already observed canonical
    /// events before registering a new waiter. For durable facts, implementations should also reuse existing
    /// durable evidence before subscription and re-check it immediately after subscription. Hard watchdog
    /// cancellation remains the caller's liveness boundary.
    /// </remarks>
    public interface IAiDeterministicLifecycleObserver
    {
        /// <summary>
        /// Waits until a canonical engine event matching the supplied criteria has been observed.
        /// </summary>
        /// <param name="criteria">The canonical event and identity filters.</param>
        /// <param name="cancellationToken">The hard watchdog or caller cancellation token.</param>
        /// <returns>The matching canonical engine event.</returns>
        Task<AiControlPlaneEvent> WaitForAsync(
            AiDeterministicLifecycleEventCriteria criteria,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a stable snapshot of the recent canonical events retained by the observer.
        /// </summary>
        /// <returns>The recent canonical events in observation order.</returns>
        IReadOnlyList<AiControlPlaneEvent> GetRecentEvents();
    }
}
