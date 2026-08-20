using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution;


namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Provides a simulated runtime scale-out provider for tests, demos, and local validation.
    /// </summary>
    /// <remarks>
    /// This provider does not create real runtime instances.
    /// It only returns a successful or failed provider result so the watcher lifecycle
    /// can be validated before adding local, HTTP, or Kubernetes providers.
    /// </remarks>
    public sealed class SimulatedAiRuntimeScaleOutProvider : IAiRuntimeScaleOutProvider
    {
        /// <summary>
        /// The provider name used by the simulated scale-out provider.
        /// </summary>
        private const string ProviderName = "simulated";

        /// <summary>
        /// Simulated provider options.
        /// </summary>
        private readonly SimulatedAiRuntimeScaleOutProviderOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="SimulatedAiRuntimeScaleOutProvider" /> class.
        /// </summary>
        /// <param name="options">The simulated provider options.</param>
        public SimulatedAiRuntimeScaleOutProvider(
            IOptions<SimulatedAiRuntimeScaleOutProviderOptions>? options = null)
        {
            this.options =
                options?.Value ?? new SimulatedAiRuntimeScaleOutProviderOptions();
        }

        /// <inheritdoc />
        public bool CanHandle(
            AiRuntimeInstanceCapacityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            if (descriptor.Metadata is not null &&
                descriptor.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                return string.Equals(
                    providerName.Trim(),
                    ProviderName,
                    StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            if (this.options.Delay > TimeSpan.Zero)
            {
                await Task
                    .Delay(
                        this.options.Delay,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!this.options.Succeed)
            {
                return new AiRuntimeScaleOutProviderResult
                {
                    Success = false,
                    Rejected = true,
                    FailureReason = this.options.FailureReason,
                    Message = this.options.FailureReason,
                    ProviderOperationId = $"simulated-scaleout-failed-{Guid.NewGuid():N}",
                    Metadata = CreateMetadata(request, "failed")
                };
            }

            var runtimeInstanceId =
                $"{this.options.RuntimeInstanceIdPrefix}-{Guid.NewGuid():N}";

            return new AiRuntimeScaleOutProviderResult
            {
                Success = true,
                Rejected = false,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderOperationId = $"simulated-scaleout-{Guid.NewGuid():N}",
                Message = $"Simulated runtime scale-out fulfilled with runtime instance '{runtimeInstanceId}'.",
                Metadata = CreateMetadata(request, "fulfilled")
            };
        }

        /// <summary>
        /// Creates provider result metadata.
        /// </summary>
        /// <param name="request">The provider request.</param>
        /// <param name="status">The simulated provider status.</param>
        /// <returns>The provider result metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutProviderRequest request,
            string status)
        {
            var metadata =
                new Dictionary<string, string>(
                    request.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                    [AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] = ProviderName,
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderStatus] = status,
                    [AiRuntimeScaleOutMetadataKeys.CamelCaseScaleOutRequestId] = request.RequestId,
                    [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                    [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId
                };

            return metadata;
        }
    }
}