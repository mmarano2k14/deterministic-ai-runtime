using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing.Grpc
{
    /// <summary>
    /// Defines stable failure reasons returned by the Runtime Pool gRPC router.
    /// </summary>
    public static class AiRuntimePoolGrpcRoutingFailureReasons
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
        /// The exact route is not a gRPC route.
        /// </summary>
        public const string TransportMismatch =
            AiRuntimePoolRoutingFailureReasons.TransportMismatch;

        /// <summary>
        /// The exact route is draining.
        /// </summary>
        public const string RouteDraining =
            AiRuntimePoolRoutingFailureReasons.RouteDraining;

        /// <summary>
        /// The exact target runtime identity is missing.
        /// </summary>
        public const string RuntimeInstanceIdMissing =
            AiRuntimePoolRoutingFailureReasons.RuntimeInstanceIdMissing;

        /// <summary>
        /// The exact runtime capacity has been suppressed as unsafe.
        /// </summary>
        public const string CapacitySuppressed =
            AiRuntimePoolRoutingFailureReasons.CapacitySuppressed;

        /// <summary>
        /// Exact gRPC forwarding failed.
        /// </summary>
        public const string ForwardingFailed =
            "runtime-pool-grpc-forwarding-failed";

        /// <summary>
        /// The outer gRPC request did not contain JSON.
        /// </summary>
        public const string RequestJsonMissing =
            "runtime-pool-grpc-request-json-missing";

        /// <summary>
        /// The outer gRPC request contained invalid JSON.
        /// </summary>
        public const string RequestJsonInvalid =
            "runtime-pool-grpc-request-json-invalid";

        /// <summary>
        /// The child gRPC response did not contain JSON.
        /// </summary>
        public const string ResponseJsonMissing =
            "runtime-pool-grpc-response-json-missing";

        /// <summary>
        /// The child gRPC response contained invalid JSON.
        /// </summary>
        public const string ResponseJsonInvalid =
            "runtime-pool-grpc-response-json-invalid";
    }
}
