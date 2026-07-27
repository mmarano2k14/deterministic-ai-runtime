namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Http
{
    /// <summary>
    /// Defines stable failure reasons returned by the Runtime Pool HTTP router.
    /// </summary>
    public static class AiRuntimePoolHttpRoutingFailureReasons
    {
        /// <summary>
        /// The exact runtime route was not found.
        /// </summary>
        public const string RouteNotFound =
            "runtime-pool-route-not-found";

        /// <summary>
        /// The route belongs to another pool.
        /// </summary>
        public const string PoolMismatch =
            "runtime-pool-route-pool-mismatch";

        /// <summary>
        /// The route belongs to another host incarnation.
        /// </summary>
        public const string HostMismatch =
            "runtime-pool-route-host-mismatch";

        /// <summary>
        /// The exact route is not an HTTP route.
        /// </summary>
        public const string TransportMismatch =
            "runtime-pool-route-transport-mismatch";

        /// <summary>
        /// The exact route is draining.
        /// </summary>
        public const string RouteDraining =
            "runtime-pool-route-draining";

        /// <summary>
        /// The exact runtime capacity has been suppressed as unsafe.
        /// </summary>
        public const string CapacitySuppressed =
            "runtime-pool-capacity-suppressed";

        /// <summary>
        /// The exact HTTP child transport failed.
        /// </summary>
        public const string ForwardingFailed =
            "runtime-pool-http-forwarding-failed";

        /// <summary>
        /// The exact target runtime identity is missing.
        /// </summary>
        public const string RuntimeInstanceIdMissing =
            "runtime-pool-runtime-instance-id-missing";
    }
}
