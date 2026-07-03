namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Defines constants used by the gRPC runtime instance provider.
    /// </summary>
    public static class AiGrpcRuntimeProviderConstants
    {
        /// <summary>
        /// The runtime instance provider name used for gRPC runtime instances.
        /// </summary>
        public const string ProviderName = "grpc";

        /// <summary>
        /// The runtime transport name used for gRPC runtime instances.
        /// </summary>
        public const string TransportName = "grpc";

        /// <summary>
        /// The default runtime instance id prefix used by gRPC process-host runtime instances.
        /// </summary>
        public const string DefaultRuntimeInstanceIdPrefix = "grpc-runtime";

        /// <summary>
        /// The metadata key used to store the gRPC transport endpoint.
        /// </summary>
        public const string TransportEndpointMetadataKey = "transport.endpoint";

        /// <summary>
        /// The metadata key used to store the gRPC transport name.
        /// </summary>
        public const string TransportNameMetadataKey = "transport.name";
    }
}