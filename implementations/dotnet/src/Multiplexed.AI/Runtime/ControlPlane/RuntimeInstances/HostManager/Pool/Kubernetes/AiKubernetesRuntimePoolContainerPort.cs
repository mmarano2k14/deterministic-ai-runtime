namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Describes one named container port exposed by a Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolContainerPort
    {
        /// <summary>
        /// Gets the DNS-label-safe Kubernetes port name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the TCP container port.
        /// </summary>
        public int Port { get; init; }

        /// <summary>
        /// Gets the runtime instance associated with the port, or <see langword="null"/>
        /// for the stable pool endpoint.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }
    }
}
