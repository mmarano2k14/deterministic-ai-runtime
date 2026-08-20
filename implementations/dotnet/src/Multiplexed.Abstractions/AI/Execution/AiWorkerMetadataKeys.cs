namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines canonical metadata keys associated with execution workers.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names shared by execution,
    /// control-plane, policy, and observability components.
    /// </remarks>
    public static class AiWorkerMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the worker identifier.
        /// </summary>
        public const string WorkerId = "worker.id";

        /// <summary>
        /// Gets the camel-case worker identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseWorkerId = "workerId";
    }
}
