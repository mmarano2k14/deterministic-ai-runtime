namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Defines why claimed recovery transition authority was rejected.
    /// </summary>
    public enum AiRuntimePoolRecoveryExecutionAuthorityFailure
    {
        /// <summary>
        /// The supplied claim result was not acquired by this caller.
        /// </summary>
        ClaimNotAcquired = 0,

        /// <summary>
        /// The acquired result did not include its active lease.
        /// </summary>
        LeaseMissing = 1,

        /// <summary>
        /// The lease was already released.
        /// </summary>
        LeaseReleased = 2,

        /// <summary>
        /// The lease belongs to another claim.
        /// </summary>
        LeaseClaimMismatch = 3,

        /// <summary>
        /// The lease incarnation is no longer active in the claim store.
        /// </summary>
        ClaimNotActive = 4,

        /// <summary>
        /// The claim and assigned-work inventory authority differ.
        /// </summary>
        InventoryAuthorityMismatch = 5,

        /// <summary>
        /// The current inventory no longer matches the claimed fingerprint.
        /// </summary>
        InventoryFingerprintMismatch = 6,

        /// <summary>
        /// One candidate escaped the exact failed-runtime inventory boundary.
        /// </summary>
        CandidateBoundaryViolation = 7,

        /// <summary>
        /// An in-flight candidate does not carry its durable execution identifier.
        /// </summary>
        InFlightExecutionIdMissing = 8,

        /// <summary>
        /// A local-queued candidate does not carry its durable shared run identifier.
        /// </summary>
        LocalQueuedSharedRunIdMissing = 9,

        /// <summary>
        /// Ownership resolution escaped the exact candidate identity boundary.
        /// </summary>
        OwnershipBoundaryViolation = 10,

        /// <summary>
        /// The transition result escaped the exact candidate identity boundary.
        /// </summary>
        TransitionBoundaryViolation = 11
    }
}
