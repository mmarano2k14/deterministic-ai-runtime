namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Defines why Pod-wide assigned-work enumeration was rejected.
    /// </summary>
    public enum AiKubernetesRuntimePoolPodAssignedWorkFailure
    {
        /// <summary>
        /// No authoritative child suppression exists for the requested Pod UID.
        /// </summary>
        SuppressionSetMissing = 0,

        /// <summary>
        /// One or more child suppressions belong to another failure identity.
        /// </summary>
        FailureIdentityMismatch = 1,

        /// <summary>
        /// One or more child suppressions cross the requested Runtime Pool boundary.
        /// </summary>
        PoolBoundaryViolation = 2,

        /// <summary>
        /// One or more child suppressions cross the requested Pod UID boundary.
        /// </summary>
        PodBoundaryViolation = 3,

        /// <summary>
        /// The suppression set contains a duplicate runtime identity.
        /// </summary>
        DuplicateRuntimeIdentity = 4,

        /// <summary>
        /// The suppression set contains a duplicate route identity.
        /// </summary>
        DuplicateRouteIdentity = 5,

        /// <summary>
        /// A per-runtime inventory does not exactly match its authoritative suppression.
        /// </summary>
        RuntimeInventoryMismatch = 6,

        /// <summary>
        /// The same local run appears under more than one failed child runtime.
        /// </summary>
        DuplicateLocalRunIdentity = 7,

        /// <summary>
        /// The same durable execution appears under more than one failed child runtime.
        /// </summary>
        DuplicateExecutionIdentity = 8
    }
}
