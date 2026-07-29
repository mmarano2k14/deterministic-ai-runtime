namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Defines the logical capacity topology used to host and reuse runtime instances.
    /// </summary>
    public enum AiRuntimeCapacityTopologyMode
    {
        /// <summary>
        /// Leaves the topology unspecified so existing configurations preserve their historical behavior.
        /// </summary>
        Unspecified = 0,

        /// <summary>
        /// Uses independently materialized runtime hosts without a shared Runtime Pool lifecycle.
        /// </summary>
        SingleHost = 1,

        /// <summary>
        /// Uses a process Runtime Pool whose child runtime instances are physically materialized as processes.
        /// </summary>
        ProcessPool = 2,

        /// <summary>
        /// Uses Kubernetes Runtime Pool Pods that each host several independently registered runtime instances.
        /// </summary>
        KubernetesPool = 3
    }
}
