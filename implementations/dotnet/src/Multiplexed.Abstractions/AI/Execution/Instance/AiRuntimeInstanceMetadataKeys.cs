namespace Multiplexed.Abstractions.AI.Runtime.Execution.Instance
{
    /// <summary>
    /// Defines canonical metadata keys associated with runtime instance identity.
    /// </summary>
    /// <remarks>
    /// These keys expose stable runtime instance identity across execution, control-plane,
    /// observability, queue, routing, and recovery metadata without assigning ownership to
    /// any specific transport or provider implementation.
    /// </remarks>
    public static class AiRuntimeInstanceMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key carrying the runtime instance identifier.
        /// </summary>
        public const string RuntimeInstanceId = "runtime.instance.id";
        /// <summary>
        /// Gets the metadata key carrying the runtime instance status.
        /// </summary>
        public const string Status = "runtime.status";


        /// <summary>
        /// Gets the camel-case runtime instance identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseRuntimeInstanceId = "runtimeInstanceId";
    }
}
