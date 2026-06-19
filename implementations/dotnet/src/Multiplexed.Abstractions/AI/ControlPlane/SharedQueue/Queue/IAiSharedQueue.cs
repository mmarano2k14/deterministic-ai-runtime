using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue
{
    /// <summary>
    /// Defines a shared/global queue for pending shared runtime runs.
    /// </summary>
    /// <remarks>
    /// The shared queue stores pending run references and coordinates atomic claiming.
    ///
    /// It does not own full shared run state.
    /// Full shared run state belongs to IAiSharedRunStore.
    ///
    /// It does not execute DAG steps.
    /// It does not dispatch runs by itself.
    /// It only manages pending/claimed queue lifecycle.
    /// </remarks>
    public interface IAiSharedQueue
    {
        /// <summary>
        /// Enqueues a shared run into the shared queue.
        /// </summary>
        /// <param name="item">The shared queue item to enqueue.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The enqueued shared queue item.</returns>
        Task<AiSharedQueueItem> EnqueueAsync(
            AiSharedQueueItem item,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a shared queue item by shared run id.
        /// </summary>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The queue item, or <c>null</c> when unknown.</returns>
        Task<AiSharedQueueItem?> GetAsync(
            string sharedRunId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists shared queue items.
        /// </summary>
        /// <param name="includeTerminal">Whether terminal queue items should be included.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The shared queue items.</returns>
        Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
            bool includeTerminal = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Atomically claims one pending shared queue item.
        /// </summary>
        /// <param name="request">The claim request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>
        /// The claimed queue item, or <c>null</c> when no pending item is available.
        /// </returns>
        Task<AiSharedQueueItem?> ClaimNextAsync(
            AiSharedQueueClaimRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a claimed item as dispatched.
        /// </summary>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="claimToken">The claim token that owns the item.</param>
        /// <param name="reason">Optional reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated queue item, or <c>null</c> when update was not possible.</returns>
        Task<AiSharedQueueItem?> MarkDispatchedAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Requeues a claimed item back to pending when dispatch failed or claim should be released.
        /// </summary>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="claimToken">The claim token that owns the item.</param>
        /// <param name="reason">Optional reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated queue item, or <c>null</c> when requeue was not possible.</returns>
        Task<AiSharedQueueItem?> RequeueAsync(
            string sharedRunId,
            string claimToken,
            string? reason = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cancels a shared queue item when it is not already terminal.
        /// </summary>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="reason">Optional cancellation reason.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The updated queue item, or <c>null</c> when unknown.</returns>
        Task<AiSharedQueueItem?> CancelAsync(
            string sharedRunId,
            string? reason = null,
            CancellationToken cancellationToken = default);

    }
}