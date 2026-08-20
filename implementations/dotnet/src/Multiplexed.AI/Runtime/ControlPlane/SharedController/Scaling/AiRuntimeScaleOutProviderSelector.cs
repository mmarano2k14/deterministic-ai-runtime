using Multiplexed.Abstractions.AI.Execution;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;


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
        private const string ScaleOutProviderSelectionOperation = "runtime-scale-out-provider-selection";

        private readonly IAiRuntimeInstanceProviderRouter providerRouter;
        private readonly AiRuntimeInstanceRegistrationOptions registrationOptions;
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeScaleOutProviderSelector" /> class.
        /// </summary>
        /// <param name="providerRouter">The runtime instance provider router.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        public AiRuntimeScaleOutProviderSelector(
            IAiRuntimeInstanceProviderRouter providerRouter,
            IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions = null)
            : this(
                providerRouter,
                registrationOptions,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeScaleOutProviderSelector" /> class.
        /// </summary>
        /// <param name="providerRouter">The runtime instance provider router.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="observer">The control-plane observer.</param>
        public AiRuntimeScaleOutProviderSelector(
            IAiRuntimeInstanceProviderRouter providerRouter,
            IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions,
            IAiControlPlaneObserver observer)
        {
            this.providerRouter =
                providerRouter
                ?? throw new ArgumentNullException(nameof(providerRouter));

            this.registrationOptions =
                registrationOptions?.Value
                ?? new AiRuntimeInstanceRegistrationOptions();

            this.observer =
                observer
                ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;
            var providerName =
                ResolveProviderName(
                    request);

            await this.RecordProviderSelectionEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    providerName,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiRuntimeScaleOutMetadataKeys.CamelCaseRequestId] = request.RequestId,
                        [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                        [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                        [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                        [AiRuntimeScaleOutMetadataKeys.ProviderHint] = request.ProviderHint,
                        ["resolvedProviderName"] = providerName,
                        ["requestedTargetInstanceCount"] = request.RequestedTargetInstanceCount,
                        ["source"] = request.Source,
                        ["reason"] = request.Reason
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var descriptor =
                    CreateProviderDescriptor(
                        request,
                        providerName);

                if (!this.providerRouter.TryGetProvider<IAiRuntimeScaleOutProvider>(
                        descriptor,
                        out var provider))
                {
                    var providerNotFoundResult =
                        CreateProviderNotFoundResult(
                            request,
                            providerName);

                    await this.RecordProviderSelectionResultAsync(
                            request,
                            providerName,
                            providerNotFoundResult,
                            startedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return providerNotFoundResult;
                }

                var providerResult =
                    await provider
                        .RequestScaleOutAsync(
                            request,
                            cancellationToken)
                        .ConfigureAwait(false);

                await this.RecordProviderSelectionResultAsync(
                        request,
                        providerName,
                        providerResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return providerResult;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await this.RecordProviderSelectionEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        providerName,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        new Dictionary<string, object?>
                        {
                            [AiRuntimeScaleOutMetadataKeys.CamelCaseRequestId] = request.RequestId,
                            [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                            [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                            [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                            [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                            [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                            [AiRuntimeScaleOutMetadataKeys.ProviderHint] = request.ProviderHint,
                            ["resolvedProviderName"] = providerName,
                            ["requestedTargetInstanceCount"] = request.RequestedTargetInstanceCount,
                            [AiObservabilityMetadataKeys.DurationMs] = durationMs,
                            [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName,
                            [AiExceptionMetadataKeys.ExceptionMessage] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Records a provider selection result and returns it unchanged.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="providerName">The resolved provider name.</param>
        /// <param name="result">The provider result.</param>
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordProviderSelectionResultAsync(
            AiRuntimeScaleOutProviderRequest request,
            string providerName,
            AiRuntimeScaleOutProviderResult result,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);
            var outcome = ResolveOutcome(result);
            var eventType = result.Success
                ? AiControlPlaneEventType.OperationCompleted
                : AiControlPlaneEventType.OperationFailed;
            var failureReason = result.Success
                ? null
                : result.FailureReason ?? result.Message;

            await this.RecordProviderSelectionEventAsync(
                    eventType,
                    request,
                    providerName,
                    result,
                    outcome,
                    failureReason,
                    durationMs,
                    BuildResultProperties(request, providerName, result, durationMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a scale-out provider selection control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="providerName">The resolved provider name.</param>
        /// <param name="result">The optional provider result.</param>
        /// <param name="outcome">The optional operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordProviderSelectionEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeScaleOutProviderRequest request,
            string providerName,
            AiRuntimeScaleOutProviderResult? result,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
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
                            Operation = ScaleOutProviderSelectionOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? request.RequestId
                                    : request.CorrelationId,
                                RunId = request.SharedRunId,
                                RuntimeInstanceId = result?.RuntimeInstanceId,
                                PipelineKey = request.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRuntimeScaleOutMetadataKeys.CamelCaseRequestId] = request.RequestId,
                                    [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                                    [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                                    [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                                    [AiRuntimeScaleOutMetadataKeys.ProviderHint] = request.ProviderHint,
                                    ["resolvedProviderName"] = providerName,
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result?.RuntimeInstanceId,
                                    ["providerOperationId"] = result?.ProviderOperationId,
                                    ["success"] = result?.Success,
                                    ["rejected"] = result?.Rejected
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break provider selection.
            }
        }

        /// <summary>
        /// Builds event properties from a provider result.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="providerName">The resolved provider name.</param>
        /// <param name="result">The provider result.</param>
        /// <param name="durationMs">The operation duration.</param>
        /// <returns>The event properties.</returns>
        private static IReadOnlyDictionary<string, object?> BuildResultProperties(
            AiRuntimeScaleOutProviderRequest request,
            string providerName,
            AiRuntimeScaleOutProviderResult result,
            long durationMs)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeScaleOutMetadataKeys.CamelCaseRequestId] = request.RequestId,
                [AiRunMetadataKeys.CamelCaseSharedRunId] = request.SharedRunId,
                [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                [AiRuntimeScaleOutMetadataKeys.ProviderHint] = request.ProviderHint,
                ["resolvedProviderName"] = providerName,
                [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result.RuntimeInstanceId,
                ["providerOperationId"] = result.ProviderOperationId,
                ["success"] = result.Success,
                ["rejected"] = result.Rejected,
                [AiObservabilityMetadataKeys.FailureReason] = result.FailureReason,
                ["message"] = result.Message,
                ["requestedTargetInstanceCount"] = request.RequestedTargetInstanceCount,
                [AiObservabilityMetadataKeys.DurationMs] = durationMs
            };

            foreach (var item in result.Metadata)
            {
                properties[item.Key] = item.Value;
                properties[$"provider.{item.Key}"] = item.Value;
            }

            return properties;
        }

        /// <summary>
        /// Resolves the control-plane operation outcome from a provider result.
        /// </summary>
        /// <param name="result">The scale-out provider result.</param>
        /// <returns>The control-plane operation outcome.</returns>
        private static AiControlPlaneOperationOutcome ResolveOutcome(
            AiRuntimeScaleOutProviderResult result)
        {
            if (result.Success)
            {
                return AiControlPlaneOperationOutcome.Succeeded;
            }

            if (result.Rejected)
            {
                return AiControlPlaneOperationOutcome.Denied;
            }

            return AiControlPlaneOperationOutcome.CompletedWithIssues;
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

            return AiRuntimeInstanceProviderNames.Local;
        }

        /// <summary>
        /// Creates a synthetic capacity descriptor used only for provider capability selection.
        /// </summary>
        /// <remarks>
        /// No runtime instance may exist yet during scale-out. The descriptor is therefore
        /// intentionally synthetic and only carries the provider metadata required by the
        /// existing runtime instance provider router.
        ///
        /// The resolved provider name is authoritative and is written after copying request
        /// metadata so caller-provided metadata cannot accidentally override provider routing.
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
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in request.Metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    metadata[pair.Key] = pair.Value;
                }
            }

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                providerName;

            metadata[AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] =
                providerName;

            metadata[AiRuntimeScaleOutMetadataKeys.LegacyRequestId] =
                request.RequestId;

            metadata[AiRuntimeScaleOutMetadataKeys.LegacySharedRunId] =
                request.SharedRunId;

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                    request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                    request.TenantGroupId;
            }

            if (!string.IsNullOrWhiteSpace(request.PipelineKey))
            {
                metadata[AiPipelineMetadataKeys.Key] =
                    request.PipelineKey;
            }

            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
            {
                metadata[AiObservabilityMetadataKeys.CorrelationId] =
                    request.CorrelationId;
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

        /// <summary>
        /// Merges control-plane event properties.
        /// </summary>
        /// <param name="properties">The base event properties.</param>
        /// <param name="additionalProperties">The additional event properties.</param>
        /// <returns>The merged event properties.</returns>
        private static IReadOnlyDictionary<string, object?> MergeEventProperties(
            IReadOnlyDictionary<string, object?>? properties,
            IReadOnlyDictionary<string, object?> additionalProperties)
        {
            var merged = new Dictionary<string, object?>();

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    merged[item.Key] = item.Value;
                }
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
    }
}