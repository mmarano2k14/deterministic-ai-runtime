namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut
{
    /// <summary>
    /// Defines supported HTTP runtime scale-out modes.
    /// </summary>
    public static class AiHttpRuntimeScaleOutModes
    {
        /// <summary>
        /// Preserves the existing metadata-only HTTP scale-out behavior.
        /// </summary>
        public const string MetadataOnly = "MetadataOnly";

        /// <summary>
        /// Starts or attaches runtime instances through the provider-agnostic runtime host manager.
        /// </summary>
        public const string HostManager = "HostManager";
    }
}