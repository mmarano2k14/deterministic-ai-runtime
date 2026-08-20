using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;

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
            AiRuntimePoolRoutingFailureReasons.RouteNotFound;

        /// <summary>
        /// The route belongs to another pool.
        /// </summary>
        public const string PoolMismatch =
            AiRuntimePoolRoutingFailureReasons.PoolMismatch;

        /// <summary>
        /// The route belongs to another host incarnation.
        /// </summary>
        public const string HostMismatch =
            AiRuntimePoolRoutingFailureReasons.HostMismatch;

        /// <summary>
        /// The exact route is not an HTTP route.
        /// </summary>
        public const string TransportMismatch =
            AiRuntimePoolRoutingFailureReasons.TransportMismatch;

        /// <summary>
        /// The exact route is draining.
        /// </summary>
        public const string RouteDraining =
            AiRuntimePoolRoutingFailureReasons.RouteDraining;

        /// <summary>
        /// The exact runtime capacity has been suppressed as unsafe.
        /// </summary>
        public const string CapacitySuppressed =
            AiRuntimePoolRoutingFailureReasons.CapacitySuppressed;

        /// <summary>
        /// The exact HTTP child transport failed.
        /// </summary>
        public const string ForwardingFailed =
            "runtime-pool-http-forwarding-failed";

        /// <summary>
        /// The exact target runtime identity is missing.
        /// </summary>
        public const string RuntimeInstanceIdMissing =
            AiRuntimePoolRoutingFailureReasons.RuntimeInstanceIdMissing;
    }
}
