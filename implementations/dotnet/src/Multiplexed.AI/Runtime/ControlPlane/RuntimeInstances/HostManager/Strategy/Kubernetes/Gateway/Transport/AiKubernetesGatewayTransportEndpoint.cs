namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport
{
    /// <summary>
    /// Represents the transport endpoint exposed by the shared Kubernetes runtime Gateway.
    /// </summary>
    public sealed record AiKubernetesGatewayTransportEndpoint
    {
        /// <summary>
        /// Gets the endpoint that must be published to runtime transport providers.
        /// </summary>
        public string Endpoint { get; init; } = string.Empty;

        /// <summary>
        /// Gets the stable in-cluster Gateway Service endpoint.
        /// </summary>
        public string InternalEndpoint { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes namespace containing the Gateway Service.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the shared Kubernetes Gateway name.
        /// </summary>
        public string GatewayName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes Service name backing the Gateway.
        /// </summary>
        public string ServiceName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes Service port backing the Gateway.
        /// </summary>
        public int ServicePort { get; init; }

        /// <summary>
        /// Gets a value indicating whether <see cref="Endpoint"/> is exposed through a local kubectl port-forward.
        /// </summary>
        public bool UsesPortForward { get; init; }

        /// <summary>
        /// Gets the local port selected for kubectl port-forward, when applicable.
        /// </summary>
        public int? LocalPort { get; init; }
    }
}
