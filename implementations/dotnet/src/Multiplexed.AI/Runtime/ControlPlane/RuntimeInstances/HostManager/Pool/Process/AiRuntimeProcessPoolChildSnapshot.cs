namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents an immutable lifecycle snapshot of one runtime child process.
    /// </summary>
    public sealed record AiRuntimeProcessPoolChildSnapshot
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
        /// Gets the independent runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the child ordinal within the manager incarnation.
        /// </summary>
        public int Ordinal { get; init; }

        /// <summary>
        /// Gets the child lifecycle status.
        /// </summary>
        public AiRuntimeProcessPoolChildStatus Status { get; init; }
    }
}
