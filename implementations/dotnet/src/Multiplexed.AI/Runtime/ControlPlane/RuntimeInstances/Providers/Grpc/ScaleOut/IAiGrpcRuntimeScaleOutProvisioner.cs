using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut
{
    /// <summary>
    /// Defines the gRPC runtime scale-out provisioning boundary.
    /// </summary>
    public interface IAiGrpcRuntimeScaleOutProvisioner
    {
        /// <summary>
        /// Provisions gRPC runtime capacity for a scale-out provider request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default);
    }
}