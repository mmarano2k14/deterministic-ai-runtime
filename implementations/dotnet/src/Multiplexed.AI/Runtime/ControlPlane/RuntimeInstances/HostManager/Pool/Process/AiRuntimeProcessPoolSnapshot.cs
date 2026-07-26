using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents an immutable lifecycle snapshot of one process-host runtime pool incarnation.
    /// </summary>
    public sealed record AiRuntimeProcessPoolSnapshot
    {
        /// <summary>
        /// Gets the logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable identifier of the exact pool host incarnation.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the manager lifecycle status.
        /// </summary>
        public AiRuntimeProcessPoolManagerStatus Status { get; init; }

        /// <summary>
        /// Gets the configured minimum number of child processes.
        /// </summary>
        public int MinimumProcessCount { get; init; }

        /// <summary>
        /// Gets the configured maximum number of child processes.
        /// </summary>
        public int MaximumProcessCount { get; init; }

        /// <summary>
        /// Gets a value indicating whether the tracked child count is lower than the configured
        /// minimum process count.
        /// </summary>
        public bool IsBelowMinimumCapacity { get; init; }

        /// <summary>
        /// Gets the independently managed child-process snapshots.
        /// </summary>
        public IReadOnlyList<AiRuntimeProcessPoolChildSnapshot> Children { get; init; } =
            new List<AiRuntimeProcessPoolChildSnapshot>();
    }
}
