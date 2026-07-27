namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents one authoritative local runtime route registration.
    /// </summary>
    public sealed record AiRuntimePoolRouteRegistration
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
        /// Gets the exact independently registered runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the transport name, such as <c>http</c> or <c>grpc</c>.
        /// </summary>
        public required string TransportName { get; init; }

        /// <summary>
        /// Gets the exact child transport endpoint.
        /// </summary>
        public required string TransportEndpoint { get; init; }
    }
}
