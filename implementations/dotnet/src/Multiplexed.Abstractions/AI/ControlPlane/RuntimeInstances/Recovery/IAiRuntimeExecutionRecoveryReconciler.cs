namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery
{
    /// <summary>
    /// Reconciles recoverable runtime executions assigned to unavailable runtime instances.
    /// </summary>
    /// <remarks>
    /// This reconciler owns execution recovery only.
    /// It must not own runtime health detection, host restart, host kill, or provider lifecycle.
    ///
    /// Runtime health detection is owned by the runtime instance health reconciler.
    /// Runtime lifecycle is owned by runtime instance providers and host managers.
    ///
    /// A recoverable runtime execution may be unfinished, locally queued, in-flight,
    /// or already marked as failed when the failure is caused by an unavailable runtime instance.
    /// </remarks>
    public interface IAiRuntimeExecutionRecoveryReconciler
    {
        /// <summary>
        /// Reconciles recoverable executions assigned to unavailable runtime instances.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the reconciliation.</param>
        /// <returns>The recovery reconciliation result.</returns>
        Task<AiRuntimeExecutionRecoveryReconciliationResult> ReconcileAsync(
            CancellationToken cancellationToken = default);
    }
}