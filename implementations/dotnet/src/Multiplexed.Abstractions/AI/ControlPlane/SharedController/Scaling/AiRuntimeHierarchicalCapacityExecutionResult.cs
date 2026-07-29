namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents hierarchical capacity selection, optional runtime-slot reservation,
    /// optional existing-host process creation, and optional Runtime Pool Pod creation.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacityExecutionResult
    {
        /// <summary>
        /// Gets or sets the Step 7C selection and atomic reservation result.
        /// </summary>
        public required AiRuntimeHierarchicalCapacityReservationResult
            ReservationResult { get; set; }

        /// <summary>
        /// Gets or sets the existing-host process creation result.
        /// </summary>
        /// <remarks>
        /// This value is <see langword="null" /> unless the selected hierarchy level is
        /// <see cref="AiRuntimeCapacitySelectionLevel.ExistingPoolPodProcessCreation" />.
        /// </remarks>
        public AiRuntimePoolProcessCreationResult? ProcessCreation { get; set; }

        /// <summary>
        /// Gets or sets the new Runtime Pool Pod creation result.
        /// </summary>
        /// <remarks>
        /// This value is <see langword="null" /> unless the selected hierarchy level is
        /// <see cref="AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation" />.
        /// </remarks>
        public AiRuntimePoolPodCreationResult? PodCreation { get; set; }

        /// <summary>
        /// Gets the final hierarchical capacity decision.
        /// </summary>
        public AiRuntimeCapacitySelectionDecision Decision =>
            this.ReservationResult.Decision;

        /// <summary>
        /// Gets a value indicating whether existing-host process capacity was created.
        /// </summary>
        public bool IsProcessCreated =>
            this.ProcessCreation?.IsCreated == true;

        /// <summary>
        /// Gets a value indicating whether a new Runtime Pool Pod was created.
        /// </summary>
        public bool IsPodCreated =>
            this.PodCreation?.IsCreated == true;
    }
}
