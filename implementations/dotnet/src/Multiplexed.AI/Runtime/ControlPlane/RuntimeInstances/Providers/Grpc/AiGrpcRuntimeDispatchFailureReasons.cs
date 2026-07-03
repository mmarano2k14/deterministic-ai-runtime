namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Defines failure reasons emitted by the gRPC runtime instance provider.
    /// </summary>
    public static class AiGrpcRuntimeDispatchFailureReasons
    {
        /// <summary>
        /// The runtime instance endpoint is missing from the capacity descriptor.
        /// </summary>
        public const string EndpointMissing = "grpc-endpoint-missing";

        /// <summary>
        /// The runtime instance endpoint is invalid.
        /// </summary>
        public const string EndpointInvalid = "grpc-endpoint-invalid";

        /// <summary>
        /// The runtime instance command timed out.
        /// </summary>
        public const string CommandTimeout = "grpc-command-timeout";

        /// <summary>
        /// The runtime instance command failed because the gRPC provider is disabled.
        /// </summary>
        public const string ProviderDisabled = "grpc-provider-disabled";

        /// <summary>
        /// The runtime instance command failed because the gRPC circuit is open.
        /// </summary>
        public const string CircuitOpen = "grpc-circuit-open";

        /// <summary>
        /// The runtime instance command failed because the gRPC response was empty.
        /// </summary>
        public const string EmptyResponse = "grpc-empty-response";

        /// <summary>
        /// The runtime instance command failed because the gRPC response payload was invalid.
        /// </summary>
        public const string InvalidResponse = "grpc-invalid-response";

        /// <summary>
        /// The runtime instance command failed because the gRPC runtime endpoint was unavailable.
        /// </summary>
        public const string RuntimeUnavailable = "grpc-runtime-unavailable";
    }
}