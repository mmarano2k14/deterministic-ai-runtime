using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using System.Globalization;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Publishes runtime scale-out requests by persisting them into an <see cref="IAiRuntimeScaleOutRequestStore" />.
    /// </summary>
    /// <remarks>
    /// This publisher turns an admission-level scale-out decision into observable control-plane state.
    /// It does not create infrastructure directly and does not depend on Kubernetes or any scaler adapter.
    /// </remarks>
    public sealed class StoreBackedAiRuntimeScaleOutRequestPublisher : IAiRuntimeScaleOutRequestPublisher
    {
        /// <summary>
        /// The default runtime provider name used when no provider name is configured.
        /// </summary>
        private const string DefaultProviderName = "local";

        /// <summary>
        /// Metadata key used to override the generated scale-out request id.
        /// </summary>
        private const string ScaleOutRequestIdMetadataKey = "scaleout.requestId";

        /// <summary>
        /// Persists scale-out requests created by this publisher.
        /// </summary>
        private readonly IAiRuntimeScaleOutRequestStore store;

        /// <summary>
        /// Resolves the logical control-plane identifier when the shared run record does not already contain one.
        /// </summary>
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;

        /// <summary>
        /// The runtime instance registration options used to resolve the provider hint.
        /// </summary>
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="StoreBackedAiRuntimeScaleOutRequestPublisher" /> class.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane identifier resolver.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        public StoreBackedAiRuntimeScaleOutRequestPublisher(
            IAiRuntimeScaleOutRequestStore store,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions = null)
        {
            this.store =
                store
                ?? throw new ArgumentNullException(nameof(store));

            this.controlPlaneIdResolver =
                controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));

            this.registrationOptions =
                registrationOptions?.Value
                ?? new AiRuntimeInstanceRegistrationOptions();
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutRequestResult> PublishAsync(
            AiRuntimeScaleOutRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.SharedRun);
            ArgumentNullException.ThrowIfNull(request.ExecutionContextSnapshot);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SharedRunId);

            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            var targetInstanceCount =
                GetRequestedTargetInstanceCount(
                    request);

            var providerHint =
                this.ResolveProviderHint();

            var record = new AiRuntimeScaleOutRequestRecord
            {
                RequestId = CreateRequestId(request),
                ControlPlaneId = controlPlaneId,
                SharedRunId = request.SharedRunId,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,

                TenantId = request.TenantId,
                TenantGroupId = request.TenantGroupId,
                PipelineKey = request.PipelineKey,

                IsolationMode = request.IsolationMode,
                PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                AllowSharedFallback = request.AllowSharedFallback,
                MaxRuntimeInstances = request.MaxRuntimeInstances,
                RuntimeInstanceIdPrefix = request.RuntimeInstanceIdPrefix,
                WorkerCountPerInstance = request.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = request.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = request.LocalQueueCapacity,

                Status = AiRuntimeScaleOutRequestStatus.Pending,
                Reason = GetReason(request),

                VisibleInstanceCount = request.VisibleInstanceCount,
                AvailableInstanceCount = request.AvailableInstanceCount,
                CurrentInstanceCount = request.CurrentInstanceCount,
                MaxInstanceCount = request.MaxInstanceCount,
                RequestedTargetInstanceCount = targetInstanceCount,

                ProviderHint = providerHint,
                RequestedBy = request.RequestedBy,
                Source = request.Source,
                CorrelationId = request.CorrelationId,
                CreatedAtUtc = DateTimeOffset.UtcNow,

                Metadata = CreateMetadata(
                    request,
                    controlPlaneId,
                    providerHint)
            };

            var created =
                await this.store
                    .CreateAsync(
                        record,
                        cancellationToken)
                    .ConfigureAwait(false);

            return new AiRuntimeScaleOutRequestResult
            {
                Success = true,
                SharedRunId = request.SharedRunId,
                ScaleOutRequestId = created.RequestId,
                RequestedTargetInstanceCount = created.RequestedTargetInstanceCount,
                Message = string.Equals(created.RequestId, record.RequestId, StringComparison.Ordinal)
                    ? "Scale-out request persisted."
                    : "Scale-out request deduplicated against an existing pending request.",
                PublishedAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Resolves the logical control-plane identifier for a scale-out request.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no logical control-plane identifier can be resolved.
        /// </exception>
        private async Task<string> ResolveControlPlaneIdAsync(
            AiRuntimeScaleOutRequest request,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(request.SharedRun.ControlPlaneId))
            {
                return request.SharedRun.ControlPlaneId;
            }

            var resolved =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            throw new InvalidOperationException(
                "Scale-out request control-plane id could not be resolved.");
        }

        /// <summary>
        /// Resolves the provider hint used by the scale-out watcher to select a runtime instance provider.
        /// </summary>
        /// <returns>The resolved provider hint.</returns>
        private string ResolveProviderHint()
        {
            if (!string.IsNullOrWhiteSpace(this.registrationOptions.ProviderName))
            {
                return this.registrationOptions.ProviderName.Trim();
            }

            return DefaultProviderName;
        }

        /// <summary>
        /// Creates a scale-out request identifier.
        /// </summary>
        /// <remarks>
        /// By default, submit-time scale-out remains deterministic and uses the shared run id.
        /// Recovery and redispatch paths may provide a metadata override so they can publish
        /// a new replacement request for the same shared run after an earlier request has already
        /// reached a terminal state.
        /// </remarks>
        /// <param name="request">The scale-out request.</param>
        /// <returns>The generated scale-out request identifier.</returns>
        private static string CreateRequestId(
            AiRuntimeScaleOutRequest request)
        {
            if (request.Metadata.TryGetValue(ScaleOutRequestIdMetadataKey, out var requestId) &&
                !string.IsNullOrWhiteSpace(requestId))
            {
                return requestId.Trim();
            }

            return $"scale-out-{request.SharedRunId}";
        }

        /// <summary>
        /// Gets the reason associated with the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <returns>The scale-out reason.</returns>
        private static string GetReason(
            AiRuntimeScaleOutRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.Reason))
            {
                return request.Reason;
            }

            return "No runtime capacity was available for admission.";
        }

        /// <summary>
        /// Computes the requested target runtime instance count.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <returns>The requested target runtime instance count.</returns>
        private static int GetRequestedTargetInstanceCount(
            AiRuntimeScaleOutRequest request)
        {
            var requested =
                Math.Max(
                    request.CurrentInstanceCount + 1,
                    1);

            if (request.MaxInstanceCount.HasValue)
            {
                requested =
                    Math.Min(
                        requested,
                        request.MaxInstanceCount.Value);
            }

            return requested;
        }

        /// <summary>
        /// Creates metadata for the persisted scale-out request record.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <param name="controlPlaneId">The resolved logical control-plane identifier.</param>
        /// <param name="providerHint">The resolved provider hint.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IDictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutRequest request,
            string controlPlaneId,
            string providerHint)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in request.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    metadata[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            metadata["controlPlaneId"] = controlPlaneId;
            metadata["sharedRunId"] = request.SharedRunId;
            metadata["providerHint"] = providerHint;

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
            }

            if (!string.IsNullOrWhiteSpace(request.PipelineKey))
            {
                metadata["pipelineKey"] = request.PipelineKey;
            }

            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = request.PreferDedicatedCapacity.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = request.AllowSharedFallback.ToString();

            if (request.MaxRuntimeInstances.HasValue)
            {
                metadata["runtime.maxRuntimeInstances"] =
                    request.MaxRuntimeInstances.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix))
            {
                metadata["runtime.instanceIdPrefix"] =
                    request.RuntimeInstanceIdPrefix;
            }

            if (request.WorkerCountPerInstance.HasValue)
            {
                metadata["runtime.workerCountPerInstance"] =
                    request.WorkerCountPerInstance.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (request.MaxConcurrentRunsPerInstance.HasValue)
            {
                metadata["runtime.maxConcurrentRunsPerInstance"] =
                    request.MaxConcurrentRunsPerInstance.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (request.LocalQueueCapacity.HasValue)
            {
                metadata["runtime.localQueueCapacity"] =
                    request.LocalQueueCapacity.Value.ToString(CultureInfo.InvariantCulture);
            }

            metadata["visibleInstanceCount"] =
                request.VisibleInstanceCount.ToString(CultureInfo.InvariantCulture);

            metadata["availableInstanceCount"] =
                request.AvailableInstanceCount.ToString(CultureInfo.InvariantCulture);

            metadata["currentInstanceCount"] =
                request.CurrentInstanceCount.ToString(CultureInfo.InvariantCulture);

            if (request.MaxInstanceCount.HasValue)
            {
                metadata["maxInstanceCount"] =
                    request.MaxInstanceCount.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                metadata["correlationId"] = request.CorrelationId;
            }

            return metadata;
        }
    }
}