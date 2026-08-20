using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using System.Globalization;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.ControlPlane;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;

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

        /// <summary>
        /// The control-plane operation name used for scale-out request publication events.
        /// </summary>
        private const string RuntimeScaleOutRequestPublishOperation = "runtime-scale-out-request-publish";

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
        /// Records scale-out publication control-plane events.
        /// </summary>
        private readonly IAiControlPlaneObserver observer;

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
            : this(
                store,
                controlPlaneIdResolver,
                registrationOptions,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="StoreBackedAiRuntimeScaleOutRequestPublisher" /> class.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane identifier resolver.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="observer">The control-plane observer.</param>
        public StoreBackedAiRuntimeScaleOutRequestPublisher(
            IAiRuntimeScaleOutRequestStore store,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions,
            IAiControlPlaneObserver observer)
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

            this.observer =
                observer
                ?? throw new ArgumentNullException(nameof(observer));
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

            var startedAtUtc = DateTimeOffset.UtcNow;
            var scaleOutRequestId = CreateRequestId(request);
            var providerHint = this.ResolveProviderHint();

            await this.RecordScaleOutPublishEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    scaleOutRequestId,
                    null,
                    providerHint,
                    null,
                    null,
                    null,
                    this.BuildScaleOutPublishProperties(
                        request,
                        scaleOutRequestId,
                        null,
                        providerHint,
                        null,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var controlPlaneId =
                    await this.ResolveControlPlaneIdAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                var targetInstanceCount =
                    GetRequestedTargetInstanceCount(
                        request);

                var record = new AiRuntimeScaleOutRequestRecord
                {
                    RequestId = scaleOutRequestId,
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
                    Metadata = await this.CreateMetadataAsync(
                            request,
                            controlPlaneId,
                            providerHint,
                            cancellationToken)
                        .ConfigureAwait(false)
                };

                var created =
                    await this.store
                        .CreateAsync(
                            record,
                            cancellationToken)
                        .ConfigureAwait(false);

                var result = new AiRuntimeScaleOutRequestResult
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

                await this.RecordScaleOutPublishEventAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        request,
                        result.ScaleOutRequestId,
                        controlPlaneId,
                        providerHint,
                        AiControlPlaneOperationOutcome.Succeeded,
                        null,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        this.BuildScaleOutPublishProperties(
                            request,
                            result.ScaleOutRequestId,
                            controlPlaneId,
                            providerHint,
                            result,
                            null),
                        cancellationToken)
                    .ConfigureAwait(false);

                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await this.RecordScaleOutPublishEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        scaleOutRequestId,
                        null,
                        providerHint,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        CalculateDurationMs(startedAtUtc, DateTimeOffset.UtcNow),
                        this.BuildScaleOutPublishProperties(
                            request,
                            scaleOutRequestId,
                            null,
                            providerHint,
                            null,
                            exception),
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Records a scale-out request publication control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The scale-out request.</param>
        /// <param name="scaleOutRequestId">The scale-out request identifier.</param>
        /// <param name="controlPlaneId">The optional control-plane identifier.</param>
        /// <param name="providerHint">The provider hint.</param>
        /// <param name="outcome">The optional control-plane outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private async Task RecordScaleOutPublishEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeScaleOutRequest request,
            string scaleOutRequestId,
            string? controlPlaneId,
            string providerHint,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?> properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer
                    .RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Scaling,
                            Operation = RuntimeScaleOutRequestPublishOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? request.SharedRunId
                                    : request.CorrelationId,
                                RunId = request.SharedRunId,
                                PipelineKey = request.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRuntimeScaleOutMetadataKeys.CamelCaseScaleOutRequestId] = scaleOutRequestId,
                                    [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId ?? string.Empty,
                                    [AiRuntimeScaleOutMetadataKeys.ProviderHint] = providerHint,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                                    [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                                    [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break scale-out request publication.
            }
        }

        /// <summary>
        /// Builds scale-out publication event properties.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <param name="scaleOutRequestId">The scale-out request identifier.</param>
        /// <param name="controlPlaneId">The optional control-plane identifier.</param>
        /// <param name="providerHint">The provider hint.</param>
        /// <param name="result">The optional publication result.</param>
        /// <param name="exception">The optional exception.</param>
        /// <returns>The event properties.</returns>
        private IReadOnlyDictionary<string, object?> BuildScaleOutPublishProperties(
            AiRuntimeScaleOutRequest request,
            string scaleOutRequestId,
            string? controlPlaneId,
            string providerHint,
            AiRuntimeScaleOutRequestResult? result,
            Exception? exception)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeScaleOutMetadataKeys.CamelCaseScaleOutRequestId] = scaleOutRequestId,
                [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId ?? string.Empty,
                [AiRuntimeScaleOutMetadataKeys.ProviderHint] = providerHint,
                [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                ["source"] = request.Source,
                ["reason"] = GetReason(request),
                ["visibleInstanceCount"] = request.VisibleInstanceCount,
                ["availableInstanceCount"] = request.AvailableInstanceCount,
                ["currentInstanceCount"] = request.CurrentInstanceCount,
                ["maxInstanceCount"] = request.MaxInstanceCount,
                ["requestedTargetInstanceCount"] = GetRequestedTargetInstanceCount(request),
                ["isolationMode"] = request.IsolationMode.ToString(),
                ["preferDedicatedCapacity"] = request.PreferDedicatedCapacity,
                ["allowSharedFallback"] = request.AllowSharedFallback,
                ["runtimeInstanceIdPrefix"] = request.RuntimeInstanceIdPrefix
            };

            if (result is not null)
            {
                properties["success"] = result.Success;
                properties["message"] = result.Message;
                properties["publishedScaleOutRequestId"] = result.ScaleOutRequestId;
                properties["publishedRequestedTargetInstanceCount"] = result.RequestedTargetInstanceCount;
            }

            if (exception is not null)
            {
                properties[AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName;
                properties[AiExceptionMetadataKeys.ExceptionMessage] = exception.Message;
                properties[AiObservabilityMetadataKeys.FailureReason] = exception.GetType().Name;
            }

            foreach (var pair in request.Metadata)
            {
                properties[pair.Key] = pair.Value;
                properties[$"scaleout.{pair.Key}"] = pair.Value;
            }

            return properties;
        }

        /// <summary>
        /// Merges control-plane event properties.
        /// </summary>
        /// <param name="properties">The base event properties.</param>
        /// <param name="additionalProperties">The additional event properties.</param>
        /// <returns>The merged event properties.</returns>
        private static IReadOnlyDictionary<string, object?> MergeEventProperties(
            IReadOnlyDictionary<string, object?> properties,
            IReadOnlyDictionary<string, object?> additionalProperties)
        {
            var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in properties)
            {
                merged[item.Key] = item.Value;
            }

            foreach (var item in additionalProperties)
            {
                merged[item.Key] = item.Value;
            }

            return merged;
        }

        /// <summary>
        /// Calculates duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The start timestamp.</param>
        /// <param name="completedAtUtc">The completion timestamp.</param>
        /// <returns>The duration in milliseconds.</returns>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Resolves the logical control-plane identifier for a scale-out request.
        /// </summary>
        /// <param name="request">The scale-out request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            AiRuntimeScaleOutRequest request,
            CancellationToken cancellationToken)
        {
            var controlPlaneId =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = request.SharedRun.ControlPlaneId,
                            Metadata = request.Metadata,
                            Source = "store-backed-runtime-scale-out-request-publisher",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "Scale-out request control-plane id could not be resolved to a logical control-plane id.");
            }

            return controlPlaneId;
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

            return AiRuntimeInstanceProviderNames.Local;
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
            if (request.Metadata.TryGetValue(AiRuntimeScaleOutMetadataKeys.RequestId, out var requestId) &&
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
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The metadata dictionary.</returns>
        private async Task<IDictionary<string, string>> CreateMetadataAsync(
            AiRuntimeScaleOutRequest request,
            string controlPlaneId,
            string providerHint,
            CancellationToken cancellationToken)
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

            var controlPlaneMetadata =
                await this.controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = controlPlaneId,
                            Metadata = metadata,
                            Source = "store-backed-runtime-scale-out-request-publisher-metadata",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            foreach (var pair in controlPlaneMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            metadata[AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId;
            metadata[AiRuntimeScaleOutMetadataKeys.ProviderHint] = providerHint;

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
                metadata[AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey;
            }

            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = request.PreferDedicatedCapacity.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = request.AllowSharedFallback.ToString();

            if (request.MaxRuntimeInstances.HasValue)
            {
                metadata[AiRuntimeInstanceProvisioningMetadataKeys.MaxRuntimeInstances] =
                    request.MaxRuntimeInstances.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix))
            {
                metadata[AiRuntimeInstanceProvisioningMetadataKeys.RuntimeInstanceIdPrefix] =
                    request.RuntimeInstanceIdPrefix;
            }

            if (request.WorkerCountPerInstance.HasValue)
            {
                metadata[AiRuntimeInstanceProvisioningMetadataKeys.WorkerCountPerInstance] =
                    request.WorkerCountPerInstance.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (request.MaxConcurrentRunsPerInstance.HasValue)
            {
                metadata[AiRuntimeInstanceProvisioningMetadataKeys.MaxConcurrentRunsPerInstance] =
                    request.MaxConcurrentRunsPerInstance.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (request.LocalQueueCapacity.HasValue)
            {
                metadata[AiRuntimeInstanceProvisioningMetadataKeys.LocalQueueCapacity] =
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
                metadata[AiObservabilityMetadataKeys.CamelCaseCorrelationId] = request.CorrelationId;
            }

            return metadata;
        }
    }
}