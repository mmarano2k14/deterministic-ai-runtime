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
        /// Identifies the legacy host creation mode metadata key retained for compatibility.
        /// </summary>
        public const string LegacyHostCreationMode = "hostCreation.mode";

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

        /// <summary>
        /// Correlates one host creation request with the runtime registrations produced by it.
        /// </summary>
        /// <remarks>
        /// This value is diagnostic propagation only. Runtime identity, membership, routing, and
        /// recovery remain governed by their typed fields.
        /// </remarks>
        public const string LifecycleCorrelationId = "host.lifecycle.correlation-id";

        /// <summary>
        /// Gets the camel-case host identifier metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseHostId = "hostId";

        /// <summary>
        /// Gets the camel-case host creation mode metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseHostCreationMode = "hostCreationMode";

        /// <summary>
        /// Gets the camel-case host type metadata key used by compatibility and diagnostic payloads.
        /// </summary>
        public const string CamelCaseHostType = "hostType";

        /// <summary>
        /// Gets the host deployment metadata key used by runtime registration and compatibility payloads.
        /// </summary>
        public const string Deployment = "deployment";
    }
}
