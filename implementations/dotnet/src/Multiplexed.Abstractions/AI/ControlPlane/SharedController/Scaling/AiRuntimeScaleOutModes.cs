namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines provider-neutral runtime scale-out modes.
    /// </summary>
    public static class AiRuntimeScaleOutModes
    {
        /// <summary>Materializes runtime registration and capacity metadata without creating a host.</summary>
        public const string MetadataOnly = "MetadataOnly";

        /// <summary>Creates or attaches runtime capacity through the runtime host manager.</summary>
        public const string HostManager = "HostManager";
    }
}
