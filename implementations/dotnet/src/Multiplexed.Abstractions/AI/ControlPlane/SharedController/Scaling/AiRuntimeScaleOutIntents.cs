namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines canonical intent values used by runtime scale-out coordination.
    /// </summary>
    public static class AiRuntimeScaleOutIntents
    {
        /// <summary>
        /// Gets the scale-out intent used when shared-queue redispatch requires replacement capacity.
        /// </summary>
        public const string SharedQueueRedispatchReplacement =
            "shared-queue-redispatch-replacement";
    }
}
