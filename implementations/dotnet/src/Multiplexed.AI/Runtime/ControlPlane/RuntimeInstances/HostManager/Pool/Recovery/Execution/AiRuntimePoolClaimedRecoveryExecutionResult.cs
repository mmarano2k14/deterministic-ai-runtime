using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Represents deterministic outcomes produced while one exact recovery claim remains held.
    /// </summary>
    public sealed record AiRuntimePoolClaimedRecoveryExecutionResult
    {
        /// <summary>
        /// Gets the deterministic recovery claim identifier.
        /// </summary>
        public required string ClaimId { get; init; }

        /// <summary>
        /// Gets the failure observation identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the exact failed runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the number of candidates covered by the claim.
        /// </summary>
        public int CandidateCount { get; init; }

        /// <summary>
        /// Gets the number of transitions accepted for processing.
        /// </summary>
        public int AcceptedCount { get; init; }

        /// <summary>
        /// Gets the number of transitions that changed durable state.
        /// </summary>
        public int ChangedCount { get; init; }

        /// <summary>
        /// Gets the number of deterministic non-accepted outcomes.
        /// </summary>
        public int RejectedCount { get; init; }

        /// <summary>
        /// Gets when all deterministic candidate outcomes completed.
        /// </summary>
        public DateTimeOffset CompletedAtUtc { get; init; }

        /// <summary>
        /// Gets the ordered exact candidate outcomes.
        /// </summary>
        public IReadOnlyList<AiRuntimePoolRecoveryCandidateOutcome>
            Outcomes { get; init; } =
            Array.Empty<AiRuntimePoolRecoveryCandidateOutcome>();
    }
}
