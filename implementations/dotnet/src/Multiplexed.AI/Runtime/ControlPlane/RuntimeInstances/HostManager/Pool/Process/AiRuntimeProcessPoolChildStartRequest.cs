namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents a typed request to create one independently identifiable runtime child process.
    /// </summary>
    public sealed record AiRuntimeProcessPoolChildStartRequest
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
        /// Gets the independent runtime instance identifier assigned to the child process.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the monotonically increasing child ordinal within this manager incarnation.
        /// </summary>
        /// <remarks>
        /// The ordinal is diagnostic and ordering information. Correctness continues to rely on
        /// <see cref="RuntimeInstanceId"/>.
        /// </remarks>
        public int Ordinal { get; init; }
    }
}
