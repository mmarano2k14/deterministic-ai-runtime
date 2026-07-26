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
        /// The exact route is not a gRPC route.
        /// </summary>
        public const string TransportMismatch =
            "runtime-pool-route-transport-mismatch";

        /// <summary>
        /// The exact route is draining.
        /// </summary>
        public const string RouteDraining =
            "runtime-pool-route-draining";

        /// <summary>
        /// The exact target runtime identity is missing.
        /// </summary>
        public const string RuntimeInstanceIdMissing =
            "runtime-pool-runtime-instance-id-missing";

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
