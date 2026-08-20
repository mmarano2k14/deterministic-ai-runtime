namespace Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance
{
    /// <summary>
    /// Defines canonical metadata keys used by execution assistance coordination.
    /// </summary>
    public static class AiExecutionAssistanceMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key that identifies the primary runtime instance that owns the execution.
        /// </summary>
        public const string PrimaryRuntimeInstanceId = "primary.runtime.instance.id";

        /// <summary>
        /// Gets the metadata key that identifies the helper runtime instance assisting the execution.
        /// </summary>
        public const string HelperRuntimeInstanceId = "helper.runtime.instance.id";
    }
}
