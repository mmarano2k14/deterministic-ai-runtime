namespace Multiplexed.Abstractions.AI.Execution
{
    /// <summary>
    /// Defines canonical metadata keys associated with pipeline identity and versioning.
    /// </summary>
    /// <remarks>
    /// These keys preserve the existing physical metadata names used across execution,
    /// control-plane, persistence, recovery, policy, and observability components.
    /// </remarks>
    public static class AiPipelineMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the pipeline name.
        /// </summary>
        public const string Name = "pipeline.name";

        /// <summary>
        /// Gets the metadata key carrying the pipeline key.
        /// </summary>
        public const string Key = "pipeline.key";

        /// <summary>
        /// Gets the metadata key carrying the pipeline version.
        /// </summary>
        public const string Version = "pipeline.version";

        /// <summary>
        /// Gets the camel-case pipeline key used by compatibility, persistence, and diagnostic payloads.
        /// </summary>
        public const string CamelCasePipelineKey = "pipelineKey";

        /// <summary>
        /// Gets the camel-case pipeline name metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCasePipelineName = "pipelineName";

        /// <summary>
        /// Gets the camel-case pipeline version metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCasePipelineVersion = "pipelineVersion";
    }
}
