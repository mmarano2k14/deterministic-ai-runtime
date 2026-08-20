namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes
{
    /// <summary>
    /// Provides metadata keys that describe Kubernetes-hosted runtime instances.
    /// </summary>
    public static class AiKubernetesRuntimeHostMetadataKeys
    {
        /// <summary>
        /// Identifies the Kubernetes namespace that contains the runtime pod.
        /// </summary>
        public const string Namespace = "kubernetes.namespace";

        /// <summary>
        /// Identifies the Kubernetes pod name that hosts the runtime instance.
        /// </summary>
        public const string PodName = "kubernetes.pod.name";

        /// <summary>
        /// Identifies the immutable Kubernetes pod UID for the runtime host incarnation.
        /// </summary>
        public const string PodUid = "kubernetes.pod.uid";

        /// <summary>
        /// Identifies the Kubernetes node name that hosts the runtime pod.
        /// </summary>
        public const string NodeName = "kubernetes.node.name";

        /// <summary>
        /// Identifies the Kubernetes service name that exposes the runtime pod.
        /// </summary>
        public const string ServiceName = "kubernetes.service.name";

        /// <summary>
        /// Identifies the Kubernetes service endpoint used to reach the runtime.
        /// </summary>
        public const string ServiceEndpoint = "kubernetes.service.endpoint";

        /// <summary>
        /// Identifies the Kubernetes NodePort endpoint used to reach the runtime.
        /// </summary>
        public const string NodePortEndpoint = "kubernetes.nodePort.endpoint";

        /// <summary>
        /// Identifies the runtime-specific Kubernetes service name.
        /// </summary>
        public const string RuntimeServiceName = "kubernetes.runtime.service.name";

        /// <summary>
        /// Identifies the runtime-specific Kubernetes service port.
        /// </summary>
        public const string RuntimeServicePort = "kubernetes.runtime.service.port";

        /// <summary>
        /// Identifies how the Kubernetes transport endpoint was resolved.
        /// </summary>
        public const string TransportEndpointSource = "kubernetes.transport.endpoint.source";

        /// <summary>
        /// Identifies the Kubernetes container name that runs the runtime process.
        /// </summary>
        public const string ContainerName = "kubernetes.container.name";
        /// <summary>Gets the metadata key containing the Kubernetes Gateway name.</summary>
        public const string GatewayName = "kubernetes.gateway.name";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway namespace.</summary>
        public const string GatewayNamespace = "kubernetes.gateway.namespace";

        /// <summary>Gets the metadata key containing the Kubernetes GatewayClass name.</summary>
        public const string GatewayClassName = "kubernetes.gateway.class.name";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway listener name.</summary>
        public const string GatewayListenerName = "kubernetes.gateway.listener.name";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway listener port.</summary>
        public const string GatewayListenerPort = "kubernetes.gateway.listener.port";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway service name.</summary>
        public const string GatewayServiceName = "kubernetes.gateway.service.name";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway service namespace.</summary>
        public const string GatewayServiceNamespace = "kubernetes.gateway.service.namespace";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway service port.</summary>
        public const string GatewayServicePort = "kubernetes.gateway.service.port";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway internal endpoint.</summary>
        public const string GatewayInternalEndpoint = "kubernetes.gateway.internalEndpoint";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway transport endpoint.</summary>
        public const string GatewayTransportEndpoint = "kubernetes.gateway.transport.endpoint";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway internal transport endpoint.</summary>
        public const string GatewayTransportInternalEndpoint = "kubernetes.gateway.transport.internalEndpoint";

        /// <summary>Gets the metadata key indicating whether Kubernetes Gateway transport uses port-forwarding.</summary>
        public const string GatewayTransportUsesPortForward = "kubernetes.gateway.transport.usesPortForward";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway local forwarded port.</summary>
        public const string GatewayTransportLocalPort = "kubernetes.gateway.transport.localPort";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway route name.</summary>
        public const string GatewayRouteName = "kubernetes.gateway.route.name";

        /// <summary>Gets the metadata key containing the Kubernetes Gateway route kind.</summary>
        public const string GatewayRouteKind = "kubernetes.gateway.route.kind";

        /// <summary>Gets the metadata key containing the Kubernetes NodePort number.</summary>
        public const string NodePort = "kubernetes.nodePort";

        /// <summary>Gets the metadata key containing the Kubernetes NodePort host.</summary>
        public const string NodePortHost = "kubernetes.nodePort.host";

        /// <summary>Gets the metadata key containing the Kubernetes service DNS name.</summary>
        public const string ServiceDns = "kubernetes.service.dns";

        /// <summary>Gets the metadata key containing the Kubernetes service URL.</summary>
        public const string ServiceUrl = "kubernetes.service.url";

        /// <summary>Gets the metadata key indicating that the Kubernetes Pod already existed.</summary>
        public const string PodAlreadyExists = "kubernetes.pod.alreadyExists";

        /// <summary>Gets the metadata key indicating that the Kubernetes Service already existed.</summary>
        public const string ServiceAlreadyExists = "kubernetes.service.alreadyExists";

    }
}