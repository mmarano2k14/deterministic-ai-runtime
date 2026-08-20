
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut
{
    /// <summary>
    /// Defines supported gRPC runtime scale-out modes.
    /// </summary>
    public static class AiGrpcRuntimeScaleOutModes
    {
        /// <summary>
        /// Preserves metadata-only gRPC scale-out behavior.
        /// </summary>
        public const string MetadataOnly = AiRuntimeScaleOutModes.MetadataOnly;

        /// <summary>
        /// Starts or attaches runtime instances through the provider-agnostic runtime host manager.
        /// </summary>
        public const string HostManager = AiRuntimeScaleOutModes.HostManager;
    }
}