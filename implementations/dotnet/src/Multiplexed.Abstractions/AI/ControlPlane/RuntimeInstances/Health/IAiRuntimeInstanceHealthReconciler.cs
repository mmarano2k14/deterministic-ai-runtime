using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health
{
    /// <summary>
    /// Reconciles runtime instance health state to protect routing and dispatch safety.
    /// </summary>
    /// <remarks>
    /// This reconciler is responsible only for health-based routing protection.
    /// It must not recover executions, requeue runs, restart hosts, kill processes,
    /// move items to dead-letter queues, or modify shared run ownership.
    /// </remarks>
    public interface IAiRuntimeInstanceHealthReconciler
    {
        /// <summary>
        /// Reconciles runtime instance health state.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The health reconciliation result.</returns>
        Task<AiRuntimeInstanceHealthReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default);
    }
}