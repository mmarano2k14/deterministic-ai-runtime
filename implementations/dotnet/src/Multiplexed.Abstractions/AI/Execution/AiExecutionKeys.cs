namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines well-known keys used by the AI execution runtime.
    ///
    /// These keys centralize shared execution data and metadata names in order to:
    /// - avoid magic strings
    /// - reduce key mismatches across steps
    /// - improve discoverability and refactoring safety
    ///
    /// Keys are grouped by responsibility:
    /// - Data keys: stored in execution state data (business payload)
    /// - Metadata keys: stored in execution state metadata (runtime behavior & tracking)
    /// </summary>
    public static class AiExecutionKeys
    {
        // ---------------------------------------------------------------------
        // DATA KEYS (State.Data)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The default input key used to seed a new execution.
        /// Typically contains the initial user input or request payload.
        /// </summary>
        public const string Input = "input";

        /// <summary>
        /// The default summary output key used by summary-oriented steps.
        /// </summary>
        public const string Summary = "summary";

        /// <summary>
        /// The explicit public/business result selected by a pipeline for execution-level result projection.
        /// </summary>
        public const string Result = "result";

        // ---------------------------------------------------------------------
        // METADATA KEYS (State.Metadata)
        // ---------------------------------------------------------------------

        /// <summary>
        /// The metadata key used to store per-step execution metadata
        /// such as retry attempts, timestamps, and execution status.
        /// </summary>
        public const string StepExecutionMetadata = "ai.steps.execution";

        /// <summary>
        /// The metadata key representing the current step name being executed.
        /// Useful for logging, tracing, and observability.
        /// </summary>
        public const string CurrentStepName = "ai.current-step.name";

        /// <summary>
        /// The metadata key representing the current step key (resolution identifier).
        /// Helps correlate runtime execution with declarative pipeline definitions.
        /// </summary>
        public const string CurrentStepKey = "ai.current-step.key";

        /// <summary>
        /// The metadata key containing the declarative input mapping for the current step.
        ///
        /// This defines how the step should read data from the execution state.
        /// Example:
        /// { "query": "input", "context": "retrieved_docs" }
        /// </summary>
        public const string CurrentStepInput = "ai.current-step.input";

        /// <summary>
        /// The metadata key containing the declarative configuration for the current step.
        ///
        /// This defines runtime behavior such as:
        /// - model selection (gpt-4, claude, etc.)
        /// - parameters (temperature, maxTokens, etc.)
        /// - provider-specific options
        /// </summary>
        public const string CurrentStepConfig = "ai.current-step.config";
    }
}