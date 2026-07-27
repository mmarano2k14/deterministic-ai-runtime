namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Defines why exact assigned-work enumeration authority was rejected.
    /// </summary>
    public enum AiRuntimePoolAssignedWorkAuthorityFailure
    {
        /// <summary>
        /// The requested failure observation does not exist.
        /// </summary>
        FailureNotFound = 0,

        /// <summary>
        /// The failure scope is not one exact runtime instance.
        /// </summary>
        UnsupportedFailureScope = 1,

        /// <summary>
        /// The exact runtime capacity suppression does not exist.
        /// </summary>
        SuppressionMissing = 2,

        /// <summary>
        /// The suppression was created from another failure observation.
        /// </summary>
        FailureMismatch = 3,

        /// <summary>
        /// The suppression belongs to another route incarnation.
        /// </summary>
        RouteMismatch = 4,

        /// <summary>
        /// The durable work index returned an entry owned by another runtime instance.
        /// </summary>
        RuntimeBoundaryViolation = 5
    }
}
