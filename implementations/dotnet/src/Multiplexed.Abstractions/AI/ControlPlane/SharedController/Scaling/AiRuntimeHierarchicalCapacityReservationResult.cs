using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents one hierarchical capacity selection and runtime-slot reservation
    /// result.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacityReservationResult
    {
        /// <summary>
        /// Gets or sets the final hierarchical capacity decision.
        /// </summary>
        public required AiRuntimeCapacitySelectionDecision Decision { get; set; }

        /// <summary>
        /// Gets or sets the atomic runtime reservation result.
        /// </summary>
        /// <remarks>
        /// This value is <see langword="null" /> when the final hierarchy level does
        /// not reserve an existing runtime slot or when backpressure is applied.
        /// </remarks>
        public AiRuntimeAdmissionReservationAttemptResult? Reservation { get; set; }

        /// <summary>
        /// Gets or sets the number of inventory selection attempts performed before the
        /// final result was produced.
        /// </summary>
        public int SelectionAttemptCount { get; set; }

        /// <summary>
        /// Gets a value indicating whether an existing runtime slot was reserved.
        /// </summary>
        public bool IsReserved =>
            this.Reservation?.IsAcquired == true;
    }
}
