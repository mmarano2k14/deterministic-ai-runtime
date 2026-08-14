namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents one deterministic hierarchical runtime capacity selection decision.
    /// </summary>
    public sealed class AiRuntimeCapacitySelectionDecision
    {
        /// <summary>
        /// Gets or sets the selected hierarchy level.
        /// </summary>
        public AiRuntimeCapacitySelectionLevel Level { get; set; }

        /// <summary>
        /// Gets or sets the selected candidate.
        /// </summary>
        /// <remarks>
        /// This value is <see langword="null" /> when the decision applies
        /// <see cref="AiRuntimeCapacitySelectionLevel.Backpressure" />.
        /// </remarks>
        public AiRuntimeCapacitySelectionCandidate? Candidate { get; set; }

        /// <summary>
        /// Gets or sets the number of candidates evaluated for the decision.
        /// </summary>
        public int EvaluatedCandidateCount { get; set; }

        /// <summary>
        /// Gets or sets the deterministic decision reason.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Gets a value indicating whether the decision applies backpressure.
        /// </summary>
        public bool IsBackpressure =>
            this.Level == AiRuntimeCapacitySelectionLevel.Backpressure;
    }
}
