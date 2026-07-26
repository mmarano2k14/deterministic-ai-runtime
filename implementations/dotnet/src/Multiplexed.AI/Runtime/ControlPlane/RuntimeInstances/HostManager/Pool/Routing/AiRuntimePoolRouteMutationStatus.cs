namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Defines the result of a first-class route lifecycle mutation.
    /// </summary>
    public enum AiRuntimePoolRouteMutationStatus
    {
        /// <summary>
        /// The requested mutation was applied.
        /// </summary>
        Applied = 0,

        /// <summary>
        /// The route was already in the requested lifecycle state.
        /// </summary>
        AlreadyApplied = 1,

        /// <summary>
        /// No route exists for the requested runtime instance.
        /// </summary>
        NotFound = 2,

        /// <summary>
        /// A route exists for the runtime instance, but its authoritative route identity differs.
        /// </summary>
        IdentityMismatch = 3,

        /// <summary>
        /// The route exists but has not entered the draining state required by the operation.
        /// </summary>
        NotDraining = 4
    }
}
