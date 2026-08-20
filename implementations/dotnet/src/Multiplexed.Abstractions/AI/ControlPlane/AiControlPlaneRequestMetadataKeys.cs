namespace Multiplexed.Abstractions.AI.ControlPlane
{
    /// <summary>
    /// Defines canonical metadata keys used to identify the actor or source requesting control-plane operations.
    /// </summary>
    public static class AiControlPlaneRequestMetadataKeys
    {
        /// <summary>
        /// Gets the camel-case requester metadata key used by control-plane persistence and diagnostic payloads.
        /// </summary>
        public const string RequestedBy = "requestedBy";

        /// <summary>
        /// Gets the dotted requester metadata key retained by durable execution-control metadata.
        /// </summary>
        public const string DottedRequestedBy = "requested.by";
    }
}
