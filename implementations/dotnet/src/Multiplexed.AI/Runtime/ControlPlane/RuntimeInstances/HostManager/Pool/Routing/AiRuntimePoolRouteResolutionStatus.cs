namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Defines the result of resolving one exact runtime route.
    /// </summary>
    public enum AiRuntimePoolRouteResolutionStatus
    {
        /// <summary>
        /// The exact route was found and can accept the request.
        /// </summary>
        Resolved = 0,

        /// <summary>
        /// No route exists for the requested runtime instance.
        /// </summary>
        NotFound = 1,

        /// <summary>
        /// The runtime route belongs to another logical pool.
        /// </summary>
        PoolMismatch = 2,

        /// <summary>
        /// The runtime route belongs to another host incarnation.
        /// </summary>
        HostMismatch = 3,

        /// <summary>
        /// The runtime route uses another transport.
        /// </summary>
        TransportMismatch = 4,

        /// <summary>
        /// The exact route exists but is draining and cannot accept new requests.
        /// </summary>
        Draining = 5
    }
}
