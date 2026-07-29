namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations
{
    /// <summary>
    /// Identifies the outcome of one bounded atomic runtime admission reservation
    /// attempt.
    /// </summary>
    public enum AiRuntimeAdmissionReservationAttemptStatus
    {
        /// <summary>
        /// The requested run slots were reserved atomically.
        /// </summary>
        Acquired = 0,

        /// <summary>
        /// The reservation was rejected because it would exceed the supplied runtime
        /// capacity boundary.
        /// </summary>
        CapacityUnavailable = 1
    }
}
