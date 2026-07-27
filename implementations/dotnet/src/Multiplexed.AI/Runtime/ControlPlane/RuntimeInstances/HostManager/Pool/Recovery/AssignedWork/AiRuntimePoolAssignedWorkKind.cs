namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Defines the deterministic recovery priority of one assigned work candidate.
    /// </summary>
    public enum AiRuntimePoolAssignedWorkKind
    {
        /// <summary>
        /// A durable execution already exists and must be considered first.
        /// </summary>
        InFlight = 0,

        /// <summary>
        /// The runtime-local queue item has not created a durable execution yet.
        /// </summary>
        LocalQueued = 1,

        /// <summary>
        /// The index entry is recoverable but does not match a more specific category.
        /// </summary>
        OtherRecoverable = 2
    }
}
