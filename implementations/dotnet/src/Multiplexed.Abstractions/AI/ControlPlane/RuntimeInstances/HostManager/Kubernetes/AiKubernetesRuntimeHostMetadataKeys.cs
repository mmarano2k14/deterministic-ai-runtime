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
        /// Identifies the Kubernetes node name that hosts the runtime pod.
        /// </summary>
        public const string NodeName = "kubernetes.node.name";

        /// <summary>
        /// Identifies the Kubernetes service name that exposes the runtime pod.
        /// </summary>
        public const string ServiceName = "kubernetes.service.name";

        /// <summary>
        /// Identifies the Kubernetes container name that runs the runtime process.
        /// </summary>
        public const string ContainerName = "kubernetes.container.name";
    }
}