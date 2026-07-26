namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents an immutable snapshot of one exact runtime transport route.
    /// </summary>
    public sealed record AiRuntimePoolRouteDescriptor
    {
        /// <summary>
        /// Gets the immutable route-incarnation identifier.
        /// </summary>
        public required string RouteId { get; init; }

        /// <summary>
        /// Gets the logical runtime pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable host-incarnation identifier.
        /// </summary>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the exact runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the registered transport name.
        /// </summary>
        public required string TransportName { get; init; }

        /// <summary>
        /// Gets the exact child transport endpoint.
        /// </summary>
        public required string TransportEndpoint { get; init; }

        /// <summary>
        /// Gets the current route lifecycle status.
        /// </summary>
        public AiRuntimePoolRouteStatus Status { get; init; }
    }
}
