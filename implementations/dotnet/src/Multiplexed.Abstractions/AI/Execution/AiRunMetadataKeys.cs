namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines canonical metadata keys used to correlate runtime run identities.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical run metadata names used across worker,
    /// shared-runtime, recovery, dispatch, and observability components.
    /// </remarks>
    public static class AiRunMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the runtime-local run identifier.
        /// </summary>
        public const string RunId = "run.id";

        /// <summary>
        /// Gets the metadata key carrying the durable shared run identifier.
        /// </summary>
        public const string SharedRunId = "shared.run.id";

        /// <summary>
        /// Gets the metadata key carrying the local run identifier associated with a shared run.
        /// </summary>
        public const string LocalRunId = "local.run.id";

        /// <summary>
        /// Gets the camel-case shared run identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseSharedRunId = "sharedRunId";

        /// <summary>
        /// Gets the camel-case local run identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseLocalRunId = "localRunId";

        /// <summary>
        /// Gets the camel-case run identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseRunId = "runId";
    }
}
