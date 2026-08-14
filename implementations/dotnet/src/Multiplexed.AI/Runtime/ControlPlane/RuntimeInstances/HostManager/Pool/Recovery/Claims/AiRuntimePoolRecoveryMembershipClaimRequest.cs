namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Defines one exact multi-runtime recovery authority requested by a coordinator.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryMembershipClaimRequest
    {
        /// <summary>
        /// Gets the immutable failure observation identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable host incarnation that owns the failed membership.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the deterministic fingerprint of every exact authoritative member identity.
        /// </summary>
        public required string MembershipFingerprint { get; init; }

        /// <summary>
        /// Gets the exact number of runtime members covered by the claim.
        /// </summary>
        public int MemberCount { get; init; }

        /// <summary>
        /// Gets the deterministic fingerprint of the complete assigned-work inventory.
        /// </summary>
        public required string InventoryFingerprint { get; init; }

        /// <summary>
        /// Gets the exact number of recovery candidates covered by the claim.
        /// </summary>
        public int CandidateCount { get; init; }

        /// <summary>
        /// Gets the coordinator identity requesting the claim.
        /// </summary>
        public required string ClaimedBy { get; init; }
    }
}
