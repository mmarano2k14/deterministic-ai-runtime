using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;

namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents one deterministic Kubernetes Runtime Pool Pod creation result.
    /// </summary>
    public sealed record AiRuntimePoolPodCreationResult
    {
        /// <summary>
        /// Gets the provider-level scale-out request identifier.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the deterministic host-strategy request identifier.
        /// </summary>
        public required string HostRequestId { get; init; }

        /// <summary>
        /// Gets the deterministic primary runtime identity assigned to the new Pod.
        /// </summary>
        public required string PrimaryRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the exact Kubernetes Pod UID returned by the host strategy.
        /// </summary>
        public string? PodUid { get; init; }

        /// <summary>
        /// Gets the Pod creation outcome.
        /// </summary>
        public AiRuntimePoolPodCreationStatus Status { get; init; }

        /// <summary>
        /// Gets the host-strategy result when host creation was attempted.
        /// </summary>
        public AiRuntimeHostStartResult? HostStartResult { get; init; }

        /// <summary>
        /// Gets the independently registered runtime identities that converged inside
        /// the new Pod.
        /// </summary>
        public IReadOnlyList<string> RuntimeInstanceIds { get; init; } =
            Array.Empty<string>();

        /// <summary>
        /// Gets the failure reason when Pod creation or membership convergence was
        /// rejected.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Gets a value indicating whether the failure may be retried with the same
        /// deterministic request identity.
        /// </summary>
        public bool Retryable { get; init; }

        /// <summary>
        /// Gets a value indicating whether this invocation created and converged a Pod.
        /// </summary>
        public bool IsCreated =>
            this.Status == AiRuntimePoolPodCreationStatus.Created;

        /// <summary>
        /// Gets a value indicating whether this invocation reused a previously applied
        /// request result.
        /// </summary>
        public bool IsDeduplicated =>
            this.Status == AiRuntimePoolPodCreationStatus.AlreadyApplied;
    }
}
