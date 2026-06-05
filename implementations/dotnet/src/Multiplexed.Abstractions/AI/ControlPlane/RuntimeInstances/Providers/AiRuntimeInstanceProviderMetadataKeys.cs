namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines standard runtime instance provider metadata keys.
    /// </summary>
    public static class AiRuntimeInstanceProviderMetadataKeys
    {
        /// <summary>
        /// Metadata key used to identify the provider responsible for a runtime instance.
        /// </summary>
        public const string ProviderName = "provider.name";

        /// <summary>
        /// Metadata key used to identify the provider transport.
        /// </summary>
        public const string ProviderTransport = "provider.transport";

        /// <summary>
        /// Metadata key used to identify an HTTP or gRPC endpoint.
        /// </summary>
        public const string ProviderEndpoint = "provider.endpoint";

        /// <summary>
        /// Metadata key used to identify a Redis command queue key.
        /// </summary>
        public const string ProviderCommandQueueKey = "provider.commandQueueKey";

        /// <summary>
        /// Metadata key used to identify a Kubernetes namespace.
        /// </summary>
        public const string ProviderNamespace = "provider.namespace";

        /// <summary>
        /// Metadata key used to identify a Kubernetes pod name.
        /// </summary>
        public const string ProviderPodName = "provider.podName";

        /// <summary>
        /// Metadata key used to identify a Kubernetes service name.
        /// </summary>
        public const string ProviderServiceName = "provider.serviceName";

        /// <summary>
        /// Metadata key used to identify a Kubernetes node name.
        /// </summary>
        public const string ProviderNodeName = "provider.nodeName";

        /// <summary>
        /// Metadata key used to identify a region, zone, or deployment location.
        /// </summary>
        public const string ProviderRegion = "provider.region";
    }
}