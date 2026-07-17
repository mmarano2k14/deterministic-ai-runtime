namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway
{
    /// <summary>
    /// Defines the Gateway API route kind used for a runtime command transport.
    /// </summary>
    public enum AiKubernetesRuntimeRouteKind
    {
        /// <summary>
        /// Routes HTTP runtime command requests.
        /// </summary>
        HttpRoute = 0,

        /// <summary>
        /// Routes gRPC runtime command requests.
        /// </summary>
        GrpcRoute = 1
    }
}
