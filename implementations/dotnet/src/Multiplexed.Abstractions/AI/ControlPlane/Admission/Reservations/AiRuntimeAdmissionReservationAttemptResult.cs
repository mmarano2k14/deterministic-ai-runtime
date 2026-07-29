namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Represents the result of one bounded atomic runtime admission reservation
    /// attempt.
    /// </summary>
    public sealed class AiRuntimeAdmissionReservationAttemptResult
    {
        /// <summary>
        /// Gets or sets the reservation attempt status.
        /// </summary>
        public AiRuntimeAdmissionReservationAttemptStatus Status { get; set; }

        /// <summary>
        /// Gets or sets the runtime instance identifier targeted by the reservation.
        /// </summary>
        public string RuntimeInstanceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the number of run slots requested by the caller.
        /// </summary>
        public int RequestedRunCount { get; set; }

        /// <summary>
        /// Gets or sets the total reserved run count observed after the atomic attempt.
        /// </summary>
        /// <remarks>
        /// When the attempt is rejected, this is the count that prevented the bounded
        /// reservation from succeeding.
        /// </remarks>
        public int ReservedRunCount { get; set; }

        /// <summary>
        /// Gets or sets the maximum total reservation count accepted for the runtime
        /// instance during this attempt.
        /// </summary>
        public int MaximumReservedRunCount { get; set; }

        /// <summary>
        /// Gets a value indicating whether the bounded reservation was acquired.
        /// </summary>
        public bool IsAcquired =>
            this.Status ==
            AiRuntimeAdmissionReservationAttemptStatus.Acquired;
    }
}
