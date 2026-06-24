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
        /// Gets a value indicating whether the transition should only be validated without mutation.
        /// </summary>
        public bool DryRun { get; init; } = true;
    }
}