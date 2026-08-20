namespace Multiplexed.Abstractions.AI.Execution.Control
{
    /// <summary>
    /// Defines canonical metadata keys used by durable execution control operations.
    /// </summary>
    public static class AiExecutionControlMetadataKeys
    {
        /// <summary>
        /// Gets the metadata key that identifies the actor or source that requested the control operation.
        /// </summary>
        public const string RequestedBy = Multiplexed.Abstractions.AI.ControlPlane.AiControlPlaneRequestMetadataKeys.DottedRequestedBy;
    }
}
