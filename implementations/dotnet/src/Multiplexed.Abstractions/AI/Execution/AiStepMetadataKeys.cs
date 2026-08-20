namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines canonical metadata keys associated with DAG step identity and diagnostics.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names used across execution,
    /// policy, payload, persistence, and observability components.
    /// </remarks>
    public static class AiStepMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the step identifier.
        /// </summary>
        public const string StepId = "step.id";

        /// <summary>
        /// Gets the metadata key carrying the deterministic step key.
        /// </summary>
        public const string StepKey = "step.key";

        /// <summary>
        /// Gets the metadata key carrying the step name.
        /// </summary>
        public const string StepName = "step.name";

        /// <summary>
        /// Gets the metadata key carrying the step count.
        /// </summary>
        public const string StepCount = "step.count";

        /// <summary>
        /// Gets the camel-case step identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseStepId = "stepId";

        /// <summary>
        /// Gets the camel-case step key metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseStepKey = "stepKey";

        /// <summary>
        /// Gets the camel-case step name metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseStepName = "stepName";
    }
}
