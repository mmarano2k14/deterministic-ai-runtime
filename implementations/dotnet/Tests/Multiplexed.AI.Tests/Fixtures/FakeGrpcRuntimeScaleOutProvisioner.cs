using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake gRPC runtime scale-out provisioner used by integration tests.
    /// </summary>
    public sealed class FakeGrpcRuntimeScaleOutProvisioner : IAiGrpcRuntimeScaleOutProvisioner
    {
        /// <summary>
        /// Gets the provisioning requests observed by the fake provisioner.
        /// </summary>
        public List<AiRuntimeScaleOutProviderRequest> Requests { get; } = [];

        /// <summary>
        /// Gets or sets a value indicating whether provisioning should succeed.
        /// </summary>
        public bool Succeed { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether provisioning should be rejected.
        /// </summary>
        public bool Rejected { get; set; }

        /// <summary>
        /// Gets or sets the failure reason returned when provisioning fails.
        /// </summary>
        public string FailureReason { get; set; } = "fake-grpc-scale-out-failed";

        /// <summary>
        /// Gets or sets the runtime instance id returned by the fake provisioner.
        /// </summary>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets the transport endpoint returned by the fake provisioner.
        /// </summary>
        public string? TransportEndpoint { get; set; }

        /// <summary>
        /// Gets or sets metadata returned by the fake provisioner.
        /// </summary>
        public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request);

            var runtimeInstanceId =
                 !string.IsNullOrWhiteSpace(RuntimeInstanceId)
                     ? RuntimeInstanceId
                     : request.Metadata.TryGetValue(
                         AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId,
                         out var metadataRuntimeInstanceId) &&
                         !string.IsNullOrWhiteSpace(metadataRuntimeInstanceId)
                             ? metadataRuntimeInstanceId
                             : $"grpc-runtime-{request.RequestId}";

            var transportEndpoint =
                !string.IsNullOrWhiteSpace(TransportEndpoint)
                    ? TransportEndpoint
                    : request.Metadata.TryGetValue(
                        AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint,
                        out var metadataTransportEndpoint) &&
                        !string.IsNullOrWhiteSpace(metadataTransportEndpoint)
                            ? metadataTransportEndpoint
                            : $"http://runtime-host/{runtimeInstanceId}";

            var metadata =
                new Dictionary<string, string>(
                    request.Metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "grpc",
                    ["provider.name"] = "grpc",
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = "grpc",
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = runtimeInstanceId,
                    ["runtime.instance.id"] = runtimeInstanceId,
                    ["controlPlaneId"] = request.ControlPlaneId
                };

            foreach (var pair in Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            return Task.FromResult(
                new AiRuntimeScaleOutProviderResult
                {
                    Success = Succeed,
                    Rejected = Rejected,
                    RuntimeInstanceId = runtimeInstanceId,
                    ProviderOperationId = $"fake-grpc-scale-out-{Guid.NewGuid():N}",
                    Message = Succeed
                        ? "Fake gRPC runtime scale-out succeeded."
                        : "Fake gRPC runtime scale-out failed.",
                    FailureReason = Succeed ? null : FailureReason,
                    Metadata = metadata
                });
        }
    }
}