using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness
{
    /// <summary>
    /// Represents the result of waiting for a runtime instance to become ready.
    /// </summary>
    public sealed record AiRuntimeInstanceReadinessResult
    {
        /// <summary>
        /// Gets a value indicating whether the runtime instance is ready.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Gets the execution context snapshot carried by the readiness operation.
        /// </summary>
        /// <remarks>
        /// The execution context snapshot is the durable authority for tenant/runtime isolation.
        /// Consumers must not derive tenant ownership from metadata when this snapshot is available.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier checked by the readiness waiter.
        /// </summary>
        public string RuntimeInstanceId { get; init; } = string.Empty;

        /// <summary>
        /// Gets the provider name checked by the readiness waiter.
        /// </summary>
        public string ProviderName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional transport name checked by the readiness waiter.
        /// </summary>
        public string? TransportName { get; init; }

        /// <summary>
        /// Gets the structured failure reason when readiness failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the readiness operation timed out.
        /// </summary>
        public bool TimedOut { get; init; }

        /// <summary>
        /// Gets the optional transport endpoint checked by the readiness waiter.
        /// </summary>
        public string? TransportEndpoint { get; init; }
    }
}