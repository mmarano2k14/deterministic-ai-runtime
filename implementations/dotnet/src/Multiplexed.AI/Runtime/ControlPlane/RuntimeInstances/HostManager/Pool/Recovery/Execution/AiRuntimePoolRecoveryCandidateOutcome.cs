using System;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Represents the deterministic recovery outcome of one exact assigned-work candidate.
    /// </summary>
    public sealed record AiRuntimePoolRecoveryCandidateOutcome
    {
        /// <summary>
        /// Gets the exact assigned-work candidate.
        /// </summary>
        public required AiRuntimePoolAssignedWorkCandidate Candidate
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the read-only ownership resolution used by the transition, when applicable.
        /// </summary>
        public AiSharedRunOwnershipResolutionResult? Ownership
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the existing recovery transition result.
        /// </summary>
        public required AiRuntimeExecutionRecoveryTransitionResult
            Transition { get; init; }

        /// <summary>
        /// Gets when the deterministic candidate outcome completed.
        /// </summary>
        public DateTimeOffset CompletedAtUtc { get; init; }
    }
}
