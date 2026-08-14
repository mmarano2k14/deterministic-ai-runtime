using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents one active deterministic recovery claim for an exact runtime membership.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryMembershipClaim
    {
        /// <summary>
        /// Gets the deterministic claim identifier.
        /// </summary>
        public required string ClaimId { get; init; }

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
        /// Gets the deterministic exact-membership fingerprint.
        /// </summary>
        public required string MembershipFingerprint { get; init; }

        /// <summary>
        /// Gets the exact number of runtime members covered by the claim.
        /// </summary>
        public int MemberCount { get; init; }

        /// <summary>
        /// Gets the deterministic complete-inventory fingerprint.
        /// </summary>
        public required string InventoryFingerprint { get; init; }

        /// <summary>
        /// Gets the exact number of recovery candidates covered by the claim.
        /// </summary>
        public int CandidateCount { get; init; }

        /// <summary>
        /// Gets the coordinator identity that acquired the claim.
        /// </summary>
        public required string ClaimedBy { get; init; }

        /// <summary>
        /// Gets when the claim was acquired.
        /// </summary>
        public DateTimeOffset ClaimedAtUtc { get; init; }
    }
}
