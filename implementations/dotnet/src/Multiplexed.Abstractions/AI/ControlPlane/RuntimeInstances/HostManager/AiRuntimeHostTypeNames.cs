namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Defines canonical runtime host type names projected through host metadata.
    /// </summary>
    public static class AiRuntimeHostTypeNames
    {
        /// <summary>A standalone process runtime instance.</summary>
        public const string Process = "runtime-instance-process";
        /// <summary>A standalone Kubernetes runtime instance.</summary>
        public const string Kubernetes = "runtime-instance-kubernetes";
        /// <summary>A process runtime pool child instance.</summary>
        public const string ProcessPool = "runtime-instance-process-pool";
        /// <summary>A Kubernetes runtime pool child instance.</summary>
        public const string KubernetesPool = "runtime-instance-kubernetes-pool";
    }
}
