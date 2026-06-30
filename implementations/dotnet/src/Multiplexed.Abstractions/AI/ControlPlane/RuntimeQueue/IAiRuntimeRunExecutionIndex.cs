using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue
{
    /// <summary>
    /// Tracks the durable relationship between a local runtime queue run and
    /// the DAG execution created from that run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime run execution index is the bridge between the local runtime queue
    /// and the durable DAG execution store. It allows the control plane to resolve
    /// a local runtime <c>RunId</c> to the associated <c>ExecutionId</c> even after
    /// the local queue item has been consumed by the background controller.
    /// </para>
    /// <para>
    /// This index is not the shared/global queue. It belongs to the runtime queue
    /// layer and is used for runtime run observability, cancellation correlation,
    /// shared queue dispatch tracking, multi-instance execution, and Kubernetes-ready
    /// control-plane queries.
    /// </para>
    /// <para>
    /// Implementations must preserve the <see cref="AiRuntimeRunExecutionIndexEntry.ExecutionContextSnapshot"/>
    /// because <see cref="ExecutionContextSnapshot.TenantId"/> is the durable tenant
    /// isolation boundary. <see cref="ExecutionContextSnapshot.ContextKey"/> is volatile
    /// and must not be used as a durable partition key.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeRunExecutionIndex
    {
        /// <summary>
        /// Registers a runtime run that has been queued locally but may not yet have
        /// created a DAG execution.
        /// </summary>
        /// <param name="entry">The runtime run index entry to register.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the entry has been registered.</returns>
        Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime run as started and records the durable DAG execution identifier.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable DAG execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the entry has been updated.</returns>
        Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime run as completed and records the durable DAG execution identifier.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable DAG execution identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the entry has been updated.</returns>
        Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime run as failed.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable DAG execution identifier, when one exists.</param>
        /// <param name="failureReason">The failure reason to store on the index entry.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the entry has been updated.</returns>
        Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime run as cancelled.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable DAG execution identifier, when one exists.</param>
        /// <param name="reason">The optional cancellation reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the entry has been updated.</returns>
        Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a runtime run as requeued for recovery.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="executionId">The durable DAG execution identifier.</param>
        /// <param name="reason">The recovery reason to store on the index entry.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// <c>true</c> when the entry was transitioned to requeued-for-recovery;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This transition is used when work already assigned to an unavailable runtime
        /// instance has been safely returned to the shared queue.
        ///
        /// Implementations should reject missing entries and terminal entries such as
        /// completed, failed, cancelled, or already requeued-for-recovery entries.
        /// </remarks>
        Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the runtime run execution index entry for a local runtime run.
        /// </summary>
        /// <param name="runId">The local runtime run identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The matching index entry when it exists and is visible to the current tenant context;
        /// otherwise, <see langword="null"/>.
        /// </returns>
        Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists unfinished runtime run execution index entries assigned to the specified runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The unfinished index entries assigned to the specified runtime instance and visible
        /// to the current tenant context.
        /// </returns>
        /// <remarks>
        /// Terminal entries such as completed, failed, cancelled, and requeued-for-recovery runs
        /// must not be returned.
        ///
        /// Implementations must preserve tenant isolation when a tenant-scoped execution context
        /// is active.
        /// </remarks>
        Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all unfinished runtime run execution index entries.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The unfinished index entries visible to the current tenant context.
        /// </returns>
        /// <remarks>
        /// This method is used by control-plane recovery reconciliation to detect orphaned
        /// in-flight executions assigned to runtime instances that are no longer present
        /// in the runtime instance registry.
        /// 
        /// Terminal entries such as completed, failed, cancelled, and requeued-for-recovery runs
        /// must not be returned.
        /// 
        /// Implementations must preserve tenant isolation when a tenant-scoped execution context
        /// is active.
        /// </remarks>
        Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedAsync(
            CancellationToken cancellationToken = default);
    }
}