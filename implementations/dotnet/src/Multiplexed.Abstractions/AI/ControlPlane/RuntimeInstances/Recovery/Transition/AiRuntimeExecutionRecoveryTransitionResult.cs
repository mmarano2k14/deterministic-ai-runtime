namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Represents the result of applying a runtime execution recovery transition.
    /// </summary>
    public sealed class AiRuntimeExecutionRecoveryTransitionResult
    {
        /// <summary>
        /// Gets a value indicating whether the transition was accepted for processing.
        /// </summary>
        public bool Accepted { get; init; }

        /// <summary>
        /// Gets a value indicating whether a mutation was applied.
        /// </summary>
        public bool Changed { get; init; }

        /// <summary>
        /// Gets the shared run identifier.
        /// </summary>
        public string? SharedRunId { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the local runtime run identifier.
        /// </summary>
        public string? LocalRunId { get; init; }

        /// <summary>
        /// Gets the durable execution identifier.
        /// </summary>
        public string? ExecutionId { get; init; }

        /// <summary>
        /// Gets the transition action.
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Gets the reason associated with the transition result.
        /// </summary>
        public required string Reason { get; init; }
    }
}