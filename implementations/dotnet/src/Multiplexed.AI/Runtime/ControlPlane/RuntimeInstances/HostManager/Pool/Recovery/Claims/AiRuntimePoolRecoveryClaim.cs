using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Represents one active deterministic recovery claim.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryClaim
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
        /// Gets the logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable host-incarnation identifier.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the exact failed runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the exact failed route incarnation.
        /// </summary>
        public required string RouteId { get; init; }

        /// <summary>
        /// Gets the deterministic exact-inventory fingerprint.
        /// </summary>
        public required string InventoryFingerprint { get; init; }

        /// <summary>
        /// Gets the number of candidates covered by the claim.
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
