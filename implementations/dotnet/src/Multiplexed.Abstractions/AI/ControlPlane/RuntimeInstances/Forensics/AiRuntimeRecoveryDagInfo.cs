namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Describes the DAG recovery proof for one resumed execution.
    /// </summary>
    public sealed record AiRuntimeRecoveryDagInfo
    {
        /// <summary>
        /// Gets the total number of DAG steps when known.
        /// </summary>
        public int? StepCount { get; init; }

        /// <summary>
        /// Gets the number of completed steps before recovery.
        /// </summary>
        public int? CompletedStepsBeforeRecovery { get; init; }

        /// <summary>
        /// Gets the DAG step from which recovery resumed.
        /// </summary>
        public string? RecoveredFromStep { get; init; }

        /// <summary>
        /// Gets the final number of completed steps.
        /// </summary>
        public int? FinalCompletedSteps { get; init; }

        /// <summary>
        /// Gets a value indicating whether already completed steps were replayed.
        /// </summary>
        public bool? CompletedStepsReplayed { get; init; }

        /// <summary>
        /// Gets the final DAG outcome.
        /// </summary>
        public string? Outcome { get; init; }
    }
}