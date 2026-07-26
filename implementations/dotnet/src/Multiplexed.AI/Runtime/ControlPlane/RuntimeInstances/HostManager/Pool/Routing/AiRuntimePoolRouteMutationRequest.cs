namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents an exact lifecycle mutation for one route incarnation.
    /// </summary>
    public sealed record AiRuntimePoolRouteMutationRequest
    {
        /// <summary>
        /// Gets the immutable route-incarnation identifier.
        /// </summary>
        public required string RouteId { get; init; }

        /// <summary>
        /// Gets the expected logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the expected immutable host-incarnation identifier.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the exact runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }
    }
}
