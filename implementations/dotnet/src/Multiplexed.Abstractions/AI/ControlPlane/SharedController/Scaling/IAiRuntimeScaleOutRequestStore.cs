namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Persists and queries runtime scale-out requests produced by the control plane.
    /// </summary>
    /// <remarks>
    /// This store represents live operational coordination state. It must not create
    /// infrastructure directly. External scalers can observe requests and update their
    /// lifecycle status when capacity has been provisioned, rejected, or expired.
    /// </remarks>
    public interface IAiRuntimeScaleOutRequestStore
    {
        /// <summary>
        /// Creates a runtime scale-out request.
        /// </summary>
        /// <param name="request">The request record to create.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The persisted request record.</returns>
        Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
            AiRuntimeScaleOutRequestRecord request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a runtime scale-out request by identifier.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The request record when found; otherwise, <see langword="null" />.</returns>
        Task<AiRuntimeScaleOutRequestRecord?> GetAsync(
            string requestId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists runtime scale-out requests matching the supplied query.
        /// </summary>
        /// <param name="query">The query filters.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The matching request records.</returns>
        Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists pending runtime scale-out requests matching the supplied query.
        /// </summary>
        /// <param name="query">The query filters.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pending request records.</returns>
        Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListPendingAsync(
            AiRuntimeScaleOutRequestQuery query,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a pending scale-out request as observed.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="observedBy">The actor or component that observed the request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        Task<bool> MarkObservedAsync(
            string requestId,
            string observedBy,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a scale-out request as fulfilled.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="fulfilledBy">The actor or component that fulfilled the request.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier created or made available, when known.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        Task<bool> MarkFulfilledAsync(
            string requestId,
            string fulfilledBy,
            string? runtimeInstanceId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a scale-out request as rejected.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="rejectedBy">The actor or component that rejected the request.</param>
        /// <param name="reason">The rejection reason.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        Task<bool> MarkRejectedAsync(
            string requestId,
            string rejectedBy,
            string reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a scale-out request as expired.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        Task<bool> MarkExpiredAsync(
            string requestId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marks a scale-out request as cancelled.
        /// </summary>
        /// <param name="requestId">The scale-out request identifier.</param>
        /// <param name="cancelledBy">The actor or component that cancelled the request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true" /> when the transition was applied; otherwise, <see langword="false" />.</returns>
        Task<bool> MarkCancelledAsync(
            string requestId,
            string cancelledBy,
            CancellationToken cancellationToken = default);
    }
}