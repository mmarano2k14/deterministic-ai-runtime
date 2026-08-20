namespace Multiplexed.Abstractions.AI.Observability
{
    /// <summary>
    /// Defines canonical metadata keys shared by runtime observability surfaces.
    /// </summary>
    public static class AiObservabilityMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key that identifies the correlation identifier for an observed operation.
        /// </summary>
        public const string CorrelationId = "correlation.id";

        /// <summary>
        /// Gets the camel-case correlation identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseCorrelationId = "correlationId";

        /// <summary>
        /// Gets the camel-case metadata key carrying an operation duration in milliseconds.
        /// </summary>
        public const string DurationMs = "durationMs";

        /// <summary>
        /// Gets the camel-case metadata key carrying a failure reason.
        /// </summary>
        public const string FailureReason = "failureReason";
    }
}
