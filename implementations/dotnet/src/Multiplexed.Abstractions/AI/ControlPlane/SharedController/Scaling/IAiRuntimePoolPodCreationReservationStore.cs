namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Atomically reserves physical Kubernetes Runtime Pool Pod creation capacity.
    /// </summary>
    /// <remarks>
    /// The reservation authority is scoped by first-class
    /// <c>ControlPlaneId + PoolId</c>. Distributed implementations must perform
    /// the active-Pod plus reserved-Pod limit check and reservation mutation in
    /// one atomic storage operation.
    /// </remarks>
    public interface IAiRuntimePoolPodCreationReservationStore
    {
        /// <summary>
        /// Attempts to reserve one physical Pod slot.
        /// </summary>
        Task<AiRuntimePoolPodCreationReservationAttemptResult> TryAcquireAsync(
            string controlPlaneId,
            string poolId,
            string reservationId,
            int activePodCount,
            int maximumPodCount,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Releases one exact Pod creation reservation.
        /// </summary>
        Task ReleaseAsync(
            string controlPlaneId,
            string poolId,
            string reservationId,
            CancellationToken cancellationToken = default);
    }
}
