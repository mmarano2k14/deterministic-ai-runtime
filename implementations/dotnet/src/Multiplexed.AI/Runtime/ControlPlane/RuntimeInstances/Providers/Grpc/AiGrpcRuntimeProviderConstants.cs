using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

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
        public const string ProviderName = AiRuntimeInstanceProviderNames.Grpc;

        /// <summary>
        /// The runtime transport name used for gRPC runtime instances.
        /// </summary>
        public const string TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.GrpcTransportName;

        /// <summary>
        /// The default runtime instance id prefix used by gRPC process-host runtime instances.
        /// </summary>
        public const string DefaultRuntimeInstanceIdPrefix = "grpc-runtime";

    }
}