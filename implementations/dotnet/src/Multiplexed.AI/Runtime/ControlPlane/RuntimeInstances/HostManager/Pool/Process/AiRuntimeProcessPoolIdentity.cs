namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents the first-class identity of one process-host runtime pool incarnation.
    /// </summary>
    public sealed record AiRuntimeProcessPoolIdentity
    {
        /// <summary>
        /// Gets the logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable identifier of this exact pool host incarnation.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the prefix used to generate independent runtime instance identifiers.
        /// </summary>
        public required string RuntimeInstanceIdPrefix { get; init; }
    }
}
