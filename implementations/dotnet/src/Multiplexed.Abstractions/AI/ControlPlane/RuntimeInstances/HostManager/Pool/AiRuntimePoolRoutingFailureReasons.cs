namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool
{
    /// <summary>
    /// Defines transport-neutral failure reasons shared by Runtime Pool command routers.
    /// </summary>
    public static class AiRuntimePoolRoutingFailureReasons
    {
        /// <summary>The exact runtime route was not found.</summary>
        public const string RouteNotFound = "runtime-pool-route-not-found";

        /// <summary>The route belongs to another pool.</summary>
        public const string PoolMismatch = "runtime-pool-route-pool-mismatch";

        /// <summary>The route belongs to another host incarnation.</summary>
        public const string HostMismatch = "runtime-pool-route-host-mismatch";

        /// <summary>The exact route does not use the expected transport.</summary>
        public const string TransportMismatch = "runtime-pool-route-transport-mismatch";

        /// <summary>The exact route is draining.</summary>
        public const string RouteDraining = "runtime-pool-route-draining";

        /// <summary>The exact target runtime identity is missing.</summary>
        public const string RuntimeInstanceIdMissing = "runtime-pool-runtime-instance-id-missing";

        /// <summary>The exact runtime capacity has been suppressed as unsafe.</summary>
        public const string CapacitySuppressed = "runtime-pool-capacity-suppressed";
    }
}
