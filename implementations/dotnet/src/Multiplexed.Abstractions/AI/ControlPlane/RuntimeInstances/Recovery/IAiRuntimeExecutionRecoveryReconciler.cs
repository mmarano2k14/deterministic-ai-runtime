namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Reconciles unfinished runtime executions assigned to unavailable runtime instances.
    /// </summary>
    /// <remarks>
    /// This reconciler owns execution recovery only.
    /// It must not own runtime health detection, host restart, host kill, or provider lifecycle.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by runtime instance providers and host managers.
    /// </remarks>
    public interface IAiRuntimeExecutionRecoveryReconciler
    {
        /// <summary>
        /// Reconciles unfinished executions assigned to unavailable runtime instances.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the reconciliation.</param>
        /// <returns>The recovery reconciliation result.</returns>
        Task<AiRuntimeExecutionRecoveryReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default);
    }
}