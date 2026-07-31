using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Represents a request to apply a runtime execution recovery transition.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionRequest
    {
        /// <summary>
        /// Gets the resolved shared run ownership.
        /// </summary>
        public required AiSharedRunOwnershipResolutionResult Ownership { get; init; }

        /// <summary>
        /// Gets the reason associated with the recovery transition.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets the authoritative infrastructure failure incident identifier when the transition
        /// is executed from an exact Runtime Pool recovery claim.
        /// </summary>
        public string? RuntimeFailureIncidentId { get; init; }

        /// <summary>
        /// Gets the related decision-ledger entry identifier when already known.
        /// </summary>
        public string? LedgerEntryId { get; init; }

        /// <summary>
        /// Gets the correlation identifier propagated across recovery and redispatch.
        /// </summary>
        public string? CorrelationId { get; init; }

        /// <summary>
        /// Gets the causal event identifier when already known.
        /// </summary>
        public string? CausationId { get; init; }

        /// <summary>
        /// Gets a value indicating whether the transition should only be validated without mutation.
        /// </summary>
        public bool DryRun { get; init; } = true;
    }
}