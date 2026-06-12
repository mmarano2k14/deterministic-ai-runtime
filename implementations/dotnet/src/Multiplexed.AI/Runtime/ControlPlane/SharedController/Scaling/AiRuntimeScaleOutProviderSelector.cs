using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Selects and invokes a runtime scale-out provider capability using the existing
    /// runtime instance provider router.
    /// </summary>
    /// <remarks>
    /// Scale-out happens before a new runtime instance may exist. For that reason this
    /// selector resolves providers by provider name rather than by runtime instance id.
    ///
    /// This class does not introduce a separate scale-out provider routing model.
    /// It reuses the existing runtime instance provider router and asks it for a provider
    /// that supports <see cref="IAiRuntimeScaleOutProvider" />.
    /// </remarks>
    public sealed class AiRuntimeScaleOutProviderSelector :
        IAiRuntimeScaleOutProviderSelector
    {
        /// <summary>
        /// The default provider name used when no provider can be resolved from request or options.
        /// </summary>
        private const string DefaultProviderName = "local";

        /// <summary>
        /// The runtime instance provider router.
        /// </summary>
        private readonly IAiRuntimeInstanceProviderRouter providerRouter;

        /// <summary>
        /// The runtime instance registration options.
        /// </summary>
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeScaleOutProviderSelector" /> class.
        /// </summary>
        /// <param name="providerRouter">The runtime instance provider router.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        public AiRuntimeScaleOutProviderSelector(
            IAiRuntimeInstanceProviderRouter providerRouter,
            IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions = null)
        {
            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));

            this.registrationOptions =
                registrationOptions?.Value
                ?? new AiRuntimeInstanceRegistrationOptions();
        }

        /// <inheritdoc />
        public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var providerName =
                ResolveProviderName(
                    request);

            var descriptor =
                CreateProviderDescriptor(
                    request,
                    providerName);

            if (!this.providerRouter.TryGetProvider<IAiRuntimeScaleOutProvider>(
                    descriptor,
                    out var provider))
            {
                return Task.FromResult(
                    CreateProviderNotFoundResult(
                        request,
                        providerName));
            }

            return provider.RequestScaleOutAsync(
                request,
                cancellationToken);
        }

        /// <summary>
        /// Resolves the provider name from the scale-out request or registration options.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <returns>The resolved provider name.</returns>
        private string ResolveProviderName(
            AiRuntimeScaleOutProviderRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.ProviderHint))
            {
                return request.ProviderHint.Trim();
            }

            if (!string.IsNullOrWhiteSpace(this.registrationOptions.ProviderName))
            {
                return this.registrationOptions.ProviderName.Trim();
            }

            return DefaultProviderName;
        }

        /// <summary>
        /// Creates a synthetic capacity descriptor used only for provider capability selection.
        /// </summary>
        /// <remarks>
        /// No runtime instance may exist yet during scale-out. The descriptor is therefore
        /// intentionally synthetic and only carries the provider metadata required by the
        /// existing runtime instance provider router.
        /// </remarks>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="providerName">The provider name.</param>
        /// <returns>The synthetic capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateProviderDescriptor(
            AiRuntimeScaleOutProviderRequest request,
            string providerName)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName,
                    ["scaleout.request.id"] = request.RequestId,
                    ["scaleout.shared.run.id"] = request.SharedRunId
                };

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata["tenant.id"] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.PipelineKey))
            {
                metadata["pipeline.key"] = request.PipelineKey;
            }

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                metadata["correlation.id"] = request.CorrelationId;
            }

            foreach (var pair in request.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    metadata[pair.Key] = pair.Value;
                }
            }

            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = string.Empty,
                ControlPlaneId = request.ControlPlaneId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Unknown,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Creates a rejected result when no provider capability can be resolved.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="providerName">The requested provider name.</param>
        /// <returns>The rejected scale-out provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateProviderNotFoundResult(
            AiRuntimeScaleOutProviderRequest request,
            string providerName)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = false,
                Rejected = true,
                ProviderOperationId = $"scaleout-provider-not-found-{request.RequestId}",
                FailureReason = "scale-out-provider-not-found",
                Message = $"Runtime scale-out provider '{providerName}' was not found or does not support scale-out.",
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName
                }
            };
        }
    }
}