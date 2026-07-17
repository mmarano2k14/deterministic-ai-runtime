namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Represents a shared Kubernetes Gateway endpoint resolved for runtime transport routing.
    /// </summary>
    public sealed record AiKubernetesGatewayEndpoint
    {
        /// <summary>
        /// Gets the Kubernetes namespace containing the Gateway.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes Gateway name.
        /// </summary>
        public string GatewayName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes GatewayClass name.
        /// </summary>
        public string GatewayClassName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Gateway listener name.
        /// </summary>
        public string ListenerName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Gateway listener port.
        /// </summary>
        public int ListenerPort { get; init; }

        /// <summary>
        /// Gets the Kubernetes Service name backing the Gateway.
        /// </summary>
        public string ServiceName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes namespace containing the Service backing the Gateway.
        /// </summary>
        /// <remarks>
        /// This can differ from <see cref="Namespace"/> when the Gateway controller
        /// owns its data-plane infrastructure in another namespace.
        /// </remarks>
        public string ServiceNamespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes Service port used to reach the Gateway.
        /// </summary>
        public int ServicePort { get; init; }

        /// <summary>
        /// Gets the stable in-cluster endpoint of the Gateway Service.
        /// </summary>
        public string InternalEndpoint { get; init; } = string.Empty;
    }
}
