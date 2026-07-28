namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Defines why exact Kubernetes Pod membership authority was rejected.
    /// </summary>
    public enum AiKubernetesRuntimePoolPodMembershipAuthorityFailure
    {
        /// <summary>
        /// No current runtime registry member is owned by the requested Pod UID.
        /// </summary>
        MembershipNotFound = 0,

        /// <summary>
        /// A registered member owned by the Pod UID belongs to another Runtime Pool.
        /// </summary>
        PoolBoundaryViolation = 1,

        /// <summary>
        /// A registered member does not belong to the requested Pod UID.
        /// </summary>
        PodBoundaryViolation = 2,

        /// <summary>
        /// More than one registered member claims the same RuntimeInstanceId.
        /// </summary>
        DuplicateRuntimeInstanceId = 3,

        /// <summary>
        /// Retained for numeric compatibility with the previous route-backed model.
        /// </summary>
        DuplicateRouteId = 4,

        /// <summary>
        /// A shared-registry member has incomplete first-class identity.
        /// </summary>
        InvalidRegistryIdentity = 5,

        /// <summary>
        /// Retained as an alias for source compatibility with the previous route-backed model.
        /// </summary>
        InvalidRouteIdentity = InvalidRegistryIdentity
    }
}
