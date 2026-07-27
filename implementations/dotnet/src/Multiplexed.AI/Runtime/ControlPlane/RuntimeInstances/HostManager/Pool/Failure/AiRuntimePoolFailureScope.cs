namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Defines the authoritative infrastructure boundary affected by one runtime-pool failure.
    /// </summary>
    public enum AiRuntimePoolFailureScope
    {
        /// <summary>
        /// Only one independently registered runtime instance is unsafe.
        /// </summary>
        RuntimeInstance = 0,

        /// <summary>
        /// The complete host incarnation is unsafe.
        /// </summary>
        /// <remarks>
        /// Host-wide failure handling is reserved for the future Kubernetes Pool Pod boundary.
        /// </remarks>
        Host = 1
    }
}
