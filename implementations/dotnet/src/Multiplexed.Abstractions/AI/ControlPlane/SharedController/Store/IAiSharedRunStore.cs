namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Defines a store for shared runtime controller run records.
    /// </summary>
    /// <remarks>
    /// The shared run store owns persistence for <see cref="AiSharedRunRecord"/>
    /// instances.
    ///
    /// Implementations may be:
    /// - in-memory for local tests and demos
    /// - Redis-backed for distributed Kubernetes/runtime coordination
    ///
    /// Important:
    /// The store does not execute DAG steps.
    /// It does not dispatch runs to local runtime queues.
    /// It only persists and updates shared controller run records.
    /// </remarks>
    public interface IAiSharedRunStore
    {
        /// <summary>
        /// Creates a shared run record.
        /// </summary>
        /// <param name="record">The shared run record to create.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The created shared run record.</returns>
        /// <remarks>
        /// Implementations must reject duplicate shared run identifiers.
        ///
        /// Distributed implementations should perform this operation atomically.
        /// </remarks>
        Task<AiSharedRunRecord> CreateAsync(
            AiSharedRunRecord record,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a shared run record by shared run id.
        /// </summary>
        /// <param name="sharedRunId">The shared controller run identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The shared run record, or <c>null</c> when the run is unknown.
        /// </returns>
        Task<AiSharedRunRecord?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists shared run records known by the store.
        /// </summary>
        /// <param name="includeCancelled">Whether cancelled shared runs should be included.</param>
        /// <param name="includeCompleted">Whether completed shared runs should be included.</param>
        /// <param name="includeFailed">Whether failed shared runs should be included.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared run records.</returns>
        Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
            bool includeCancelled = false,
            bool includeCompleted = false,
            bool includeFailed = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a shared run when it is not already terminal.
        /// </summary>
        /// <param name="sharedRunId">The shared controller run identifier.</param>
        /// <param name="reason">The optional cancellation reason.</param>
        /// <param name="requestedBy">The optional identity requesting cancellation.</param>
        /// <param name="source">The optional source adapter requesting cancellation.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The updated shared run record, the existing terminal record,
        /// or <c>null</c> when the run is unknown.
        /// </returns>
        /// <remarks>
        /// Distributed implementations should perform the terminal-state check
        /// and cancellation update atomically.
        /// </remarks>
        Task<AiSharedRunRecord?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            string? requestedBy = null,
            string? source = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a shared run as requeued after a scale-out request has been fulfilled.
        /// </summary>
        /// <param name="sharedRunId">The shared controller run identifier.</param>
        /// <param name="reason">The optional requeue reason.</param>
        /// <param name="metadata">The optional metadata to merge into the shared run record.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The updated shared run record, the existing terminal record,
        /// or <c>null</c> when the run is unknown.
        /// </returns>
        /// <remarks>
        /// This compatibility transition is restricted to an unassigned
        /// <see cref="AiSharedRunStatus.ScaleOutRequested"/> run.
        ///
        /// Recovery callers that need to release a run from a failed runtime assignment
        /// must use <see cref="MarkRequeuedAfterScaleOutIfCurrentAsync"/>.
        ///
        /// Distributed implementations should perform the state check and update atomically.
        /// </remarks>
        Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutAsync(
            string sharedRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically marks a shared run as requeued when its current assignment still
        /// matches the failed runtime ownership expected by the recovery operation.
        /// </summary>
        /// <param name="sharedRunId">The shared controller run identifier.</param>
        /// <param name="expectedAssignedRuntimeInstanceId">
        /// The failed runtime instance id that must still own the run.
        /// Pass <c>null</c> together with <paramref name="expectedLocalRunId"/> for an
        /// initial unassigned scale-out transition.
        /// </param>
        /// <param name="expectedLocalRunId">
        /// The failed local runtime run id that must still identify the run.
        /// Pass <c>null</c> together with
        /// <paramref name="expectedAssignedRuntimeInstanceId"/> for an initial
        /// unassigned scale-out transition.
        /// </param>
        /// <param name="reason">The optional requeue reason.</param>
        /// <param name="metadata">The optional metadata to merge into the shared run record.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The requeued record, the current record when the ownership has already changed,
        /// the existing terminal record, or <c>null</c> when the run is unknown.
        /// </returns>
        /// <remarks>
        /// The transition has two legal compare-and-set paths:
        ///
        /// - initial scale-out:
        ///   status is <see cref="AiSharedRunStatus.ScaleOutRequested"/> and the run
        ///   has no runtime or local-run assignment
        /// - crash recovery:
        ///   the persisted runtime instance id and local run id still exactly match
        ///   the failed ownership supplied by the caller
        ///
        /// A delayed callback must become an idempotent no-op after another dispatcher
        /// has assigned a replacement runtime or local run id.
        ///
        /// Distributed implementations must perform the ownership comparison and update
        /// atomically.
        /// </remarks>
        Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutIfCurrentAsync(
            string sharedRunId,
            string? expectedAssignedRuntimeInstanceId,
            string? expectedLocalRunId,
            string? reason = null,
            IReadOnlyDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a shared run as dispatched to a runtime instance.
        /// </summary>
        /// <param name="sharedRunId">The shared controller run identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance id that received the run.</param>
        /// <param name="localRunId">The local runtime queue run id returned by the target runtime instance.</param>
        /// <param name="executionId">The optional durable execution id, when already available.</param>
        /// <param name="reason">The optional dispatch reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The updated shared run record, or <c>null</c> when the run is unknown or cannot be updated.
        /// </returns>
        /// <remarks>
        /// Distributed implementations should perform this update atomically.
        /// </remarks>
        Task<AiSharedRunRecord?> MarkDispatchedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? localRunId = null,
            string? executionId = null,
            string? reason = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a shared run dispatch attempt as failed without marking the run as dispatched.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier that failed dispatch.</param>
        /// <param name="failureReason">The dispatch failure reason.</param>
        /// <param name="message">The dispatch failure message.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The updated shared run record, or <c>null</c> when the run was not found.</returns>
        Task<AiSharedRunRecord?> MarkDispatchFailedAsync(
            string sharedRunId,
            string runtimeInstanceId,
            string? failureReason,
            string? message,
            CancellationToken cancellationToken = default);
    }
}
