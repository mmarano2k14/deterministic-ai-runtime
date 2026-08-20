namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Defines canonical runtime host deployment names projected through host metadata.
    /// </summary>
    public static class AiRuntimeHostDeploymentNames
    {
        /// <summary>The process runtime pool deployment name.</summary>
        public const string ProcessPool = "process-pool";
        /// <summary>The Kubernetes runtime pool deployment name.</summary>
        public const string KubernetesPool = "kubernetes-pool";

        /// <summary>
        /// Identifies a standalone Kubernetes runtime host deployment.
        /// </summary>
        public const string KubernetesHost = "kubernetes-host";
    }
}
