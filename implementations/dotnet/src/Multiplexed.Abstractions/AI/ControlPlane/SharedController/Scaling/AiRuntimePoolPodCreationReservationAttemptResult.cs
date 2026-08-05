namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents one atomic Runtime Pool Pod creation reservation attempt.
    /// </summary>
    public sealed record AiRuntimePoolPodCreationReservationAttemptResult
    {
        /// <summary>
        /// Gets a value indicating whether the exact reservation was acquired.
        /// </summary>
        public bool Acquired { get; init; }

        /// <summary>
        /// Gets the active Pod count supplied to the atomic decision.
        /// </summary>
        public int ActivePodCount { get; init; }

        /// <summary>
        /// Gets the number of non-expired Pod creation reservations after the decision.
        /// </summary>
        public int ReservedPodCount { get; init; }

        /// <summary>
        /// Gets the configured maximum Pod count.
        /// </summary>
        public int MaximumPodCount { get; init; }
    }
}
