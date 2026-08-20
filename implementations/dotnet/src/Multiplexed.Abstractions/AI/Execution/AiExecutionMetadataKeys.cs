namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines canonical metadata keys associated with execution identity and status.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names used across execution,
    /// control-plane, persistence, metrics, and observability components. They do not
    /// change execution state or lifecycle semantics.
    /// </remarks>
    public static class AiExecutionMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the execution identifier.
        /// </summary>
        public const string ExecutionId = "execution.id";

        /// <summary>
        /// Gets the metadata key carrying the execution status.
        /// </summary>
        public const string ExecutionStatus = "execution.status";

        /// <summary>
        /// Gets the camel-case execution identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseExecutionId = "executionId";
    }
}
