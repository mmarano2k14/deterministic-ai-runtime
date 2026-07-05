namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides metadata keys that describe the runtime host lifecycle layer.
    /// </summary>
    public static class AiRuntimeHostMetadataKeys
    {
        /// <summary>
        /// Identifies the infrastructure provider that owns the runtime host lifecycle.
        /// </summary>
        public const string HostProvider = "host.provider";

        /// <summary>
        /// Identifies the host creation mode used to create or attach the runtime instance.
        /// </summary>
        public const string HostCreationMode = "host.creation.mode";

        /// <summary>
        /// Identifies the host creation strategy that produced the runtime instance.
        /// </summary>
        public const string HostCreationStrategy = "host.creation.strategy";

        /// <summary>
        /// Identifies the host instance that contains the runtime instance.
        /// </summary>
        public const string HostId = "host.id";

        /// <summary>
        /// Identifies the host name that contains the runtime instance.
        /// </summary>
        public const string HostName = "host.name";
    }
}