namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents an exact route-resolution request from a stable pool endpoint.
    /// </summary>
    public sealed record AiRuntimePoolRouteResolutionRequest
    {
        /// <summary>
        /// Gets the expected logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the expected immutable host-incarnation identifier.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the exact target runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the required transport name.
        /// </summary>
        public required string TransportName { get; init; }
    }
}
