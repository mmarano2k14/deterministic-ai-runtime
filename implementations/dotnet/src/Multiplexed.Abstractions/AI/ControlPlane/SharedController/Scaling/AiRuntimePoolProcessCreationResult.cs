namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Represents the exact result of one bounded process-capacity mutation inside an
    /// existing Runtime Pool host.
    /// </summary>
    public sealed record AiRuntimePoolProcessCreationResult
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
        /// Gets the immutable identifier of the exact Runtime Pool host incarnation.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the process-creation outcome.
        /// </summary>
        public AiRuntimePoolProcessCreationStatus Status { get; init; }

        /// <summary>
        /// Gets the authoritative child-process count observed before mutation.
        /// </summary>
        public int ProcessCountBefore { get; init; }

        /// <summary>
        /// Gets the authoritative child-process count observed after mutation.
        /// </summary>
        public int ProcessCountAfter { get; init; }

        /// <summary>
        /// Gets the authoritative maximum process count of the selected host.
        /// </summary>
        public int MaximumProcessCount { get; init; }

        /// <summary>
        /// Gets the fresh runtime instance identifiers observed after the capacity
        /// mutation.
        /// </summary>
        public IReadOnlyList<string> CreatedRuntimeInstanceIds { get; init; } =
            Array.Empty<string>();

        /// <summary>
        /// Gets a value indicating whether this invocation created process capacity.
        /// </summary>
        public bool IsCreated =>
            this.Status == AiRuntimePoolProcessCreationStatus.Created;

        /// <summary>
        /// Gets a value indicating whether the request was deduplicated after a prior
        /// successful or capacity-bounded application.
        /// </summary>
        public bool IsDeduplicated =>
            this.Status == AiRuntimePoolProcessCreationStatus.AlreadyApplied;
    }
}
