namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Represents a Kubernetes Gateway API route resolved for one runtime instance.
    /// </summary>
    public sealed record AiKubernetesRuntimeRouteResult
    {
        /// <summary>
        /// Gets the Kubernetes namespace containing the route.
        /// </summary>
        public string Namespace { get; init; } = string.Empty;

        /// <summary>
        /// Gets the shared Kubernetes Gateway name.
        /// </summary>
        public string GatewayName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Gateway listener name referenced by the route.
        /// </summary>
        public string ListenerName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the route resource name.
        /// </summary>
        public string RouteName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the route kind.
        /// </summary>
        public AiKubernetesRuntimeRouteKind RouteKind { get; init; }

        /// <summary>
        /// Gets the runtime instance id selected by the route.
        /// </summary>
        public string RuntimeInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the Kubernetes Service name backing the runtime instance.
        /// </summary>
        public string RuntimeServiceName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the backend Service port.
        /// </summary>
        public int BackendPort { get; init; }

        /// <summary>
        /// Gets the routing header name.
        /// </summary>
        public string RoutingHeaderName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the routing header value.
        /// </summary>
        public string RoutingHeaderValue { get; init; } = string.Empty;
    }
}
