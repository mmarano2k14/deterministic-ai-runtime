namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Defines why exact Pod-wide capacity suppression was rejected.
    /// </summary>
    public enum AiKubernetesRuntimePoolPodCapacitySuppressionFailure
    {
        /// <summary>
        /// The Pod incarnation is already bound to another failure identity.
        /// </summary>
        FailureIdentityConflict = 0,

        /// <summary>
        /// Existing suppression state does not equal the exact current Pod membership.
        /// </summary>
        ExistingSuppressionSetMismatch = 1,

        /// <summary>
        /// One child identity conflicts with immutable capacity suppression state.
        /// </summary>
        AtomicCapacityConflict = 2,

        /// <summary>
        /// The atomic writer returned or persisted an incomplete suppression set.
        /// </summary>
        AtomicWriteVerificationFailed = 3,

        /// <summary>
        /// Existing host suppression state crosses the requested Runtime Pool boundary.
        /// </summary>
        PoolBoundaryViolation = 4
    }
}
