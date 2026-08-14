namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Defines exact failure reasons while restoring Kubernetes Runtime Pool Pod capacity.
    /// </summary>
    public enum AiKubernetesRuntimePoolPodReplacementFailure
    {
        /// <summary>
        /// The caller does not own the active exact-membership recovery lease.
        /// </summary>
        InactiveRecoveryLease = 0,

        /// <summary>
        /// The claimed inventory no longer matches its deterministic claim authority.
        /// </summary>
        ClaimAuthorityMismatch = 1,

        /// <summary>
        /// The replacement host template does not match the failed Pool authority.
        /// </summary>
        InvalidHostTemplate = 2,

        /// <summary>
        /// No unique Kubernetes Runtime Pool host strategy is available.
        /// </summary>
        HostCreationStrategyUnavailable = 3,

        /// <summary>
        /// Kubernetes rejected or failed the replacement host start operation.
        /// </summary>
        HostStartRejected = 4,

        /// <summary>
        /// The replacement operation did not return a first-class Pod UID.
        /// </summary>
        ReplacementPodUidMissing = 5,

        /// <summary>
        /// Kubernetes returned the failed Pod UID instead of a fresh Pod incarnation.
        /// </summary>
        FailedPodUidReused = 6,

        /// <summary>
        /// Replacement route membership did not converge before the readiness deadline.
        /// </summary>
        MembershipReadinessTimeout = 7,

        /// <summary>
        /// Replacement membership crossed the exact Pool or Pod boundary.
        /// </summary>
        MembershipAuthorityMismatch = 8,

        /// <summary>
        /// Replacement membership reused a runtime identity from the failed Pod.
        /// </summary>
        StaleRuntimeIdentityReused = 9,

        /// <summary>
        /// Replacement membership reused a route incarnation from the failed Pod.
        /// </summary>
        StaleRouteIdentityReused = 10,

        /// <summary>
        /// The provider-selected fresh primary runtime did not register in the new Pod.
        /// </summary>
        PrimaryRuntimeMissing = 11
    }
}
