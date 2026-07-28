namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Describes one exact RuntimeInstanceOnly child planned inside a Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodRuntimeInstanceOptions
    {
        /// <summary>
        /// Gets or sets the one-based child ordinal.
        /// </summary>
        public int Ordinal { get; set; }

        /// <summary>
        /// Gets or sets the exact independently selectable runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the exact loopback transport port assigned to the child.
        /// </summary>
        public int TransportPort { get; set; }
    }
}
