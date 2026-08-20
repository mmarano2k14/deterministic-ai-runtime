using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;


namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Selects the host creation strategy matching the requested host creation mode.
    /// </summary>
    public sealed class AiRuntimeHostCreationManager : IAiRuntimeHostManager
    {
        private const string RuntimeHostCreationOperation = "runtime-host-creation";

        /// <summary>
        /// The registered host creation strategies indexed by host creation mode.
        /// </summary>
        private readonly IReadOnlyDictionary<AiRuntimeHostCreationMode, IAiRuntimeHostCreationStrategy> strategies;

        /// <summary>
        /// The logger used to report host creation selection failures.
        /// </summary>
        private readonly ILogger<AiRuntimeHostCreationManager> logger;

        /// <summary>
        /// The control-plane observer.
        /// </summary>
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// The durable runtime infrastructure lifecycle journal.
        /// </summary>
        private readonly IAiRuntimeLifecycleJournal lifecycleJournal;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategies"/> or <paramref name="logger"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger)
            : this(
                strategies,
                logger,
                new NoopAiControlPlaneObserver(),
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategies"/>, <paramref name="logger"/>, or <paramref name="observer"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger,
            IAiControlPlaneObserver observer)
            : this(
                strategies,
                logger,
                observer,
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <param name="lifecycleJournal">The runtime infrastructure lifecycle journal.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger,
            IAiControlPlaneObserver observer,
            IAiRuntimeLifecycleJournal lifecycleJournal)
        {
            ArgumentNullException.ThrowIfNull(strategies);

            var strategyList = strategies.ToList();
            var duplicatedMode = strategyList
                .GroupBy(strategy => strategy.Mode)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicatedMode is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple runtime host creation strategies are registered for mode '{duplicatedMode.Key}'.");
            }

            this.strategies = strategyList.ToDictionary(strategy => strategy.Mode);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
            this.lifecycleJournal = lifecycleJournal ?? throw new ArgumentNullException(nameof(lifecycleJournal));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;
            var lifecycleCorrelationId = ResolveLifecycleCorrelationId(request);
            var strategyRequest = request with
            {
                Metadata = MergeLifecycleRequestMetadata(
                    request.Metadata,
                    lifecycleCorrelationId,
                    request.HostCreationMode)
            };
            var requestedLifecycleEvent = await this.AppendHostLifecycleEventAsync(
                    AiRuntimeLifecycleEventType.HostCreationRequested,
                    request,
                    result: null,
                    lifecycleCorrelationId,
                    causationId: null,
                    previousStatus: null,
                    currentStatus: "requested",
                    reason: null,
                    metadata: null,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.RecordHostCreationEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                        [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = request.ProviderName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = request.TransportName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = request.TransportEndpoint,
                        [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                        ["strategyCount"] = this.strategies.Count,
                        ["registeredModes"] = string.Join(",", this.strategies.Keys.Select(mode => mode.ToString()))
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!this.strategies.TryGetValue(request.HostCreationMode, out var strategy))
            {
                this.logger.LogWarning(
                    "No runtime host creation strategy is registered for mode {HostCreationMode}. RuntimeInstanceId={RuntimeInstanceId}, ProviderName={ProviderName}.",
                    request.HostCreationMode,
                    request.RuntimeInstanceId,
                    request.ProviderName);

                var rejectedResult =
                    AiRuntimeHostStartResult.Rejected(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        $"runtime-host-creation-mode-not-registered:{request.HostCreationMode}");

                await this.RecordHostCreationResultAsync(
                        request,
                        rejectedResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.AppendHostLifecycleEventAsync(
                        AiRuntimeLifecycleEventType.HostCreationFailed,
                        request,
                        rejectedResult,
                        lifecycleCorrelationId,
                        requestedLifecycleEvent.EventId,
                        previousStatus: "requested",
                        currentStatus: "failed",
                        reason: rejectedResult.FailureReason,
                        metadata: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                return rejectedResult;
            }

            var startedLifecycleEvent = await this.AppendHostLifecycleEventAsync(
                    AiRuntimeLifecycleEventType.HostCreationStarted,
                    request,
                    result: null,
                    lifecycleCorrelationId,
                    requestedLifecycleEvent.EventId,
                    previousStatus: "requested",
                    currentStatus: "started",
                    reason: null,
                    metadata: new Dictionary<string, string>
                    {
                        ["strategyName"] = strategy.GetType().Name
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var result =
                    await strategy
                        .StartAsync(
                            strategyRequest,
                            cancellationToken)
                        .ConfigureAwait(false);

                await this.RecordHostCreationResultAsync(
                        request,
                        result,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.AppendHostLifecycleEventAsync(
                        result.Success
                            ? AiRuntimeLifecycleEventType.HostCreationSucceeded
                            : AiRuntimeLifecycleEventType.HostCreationFailed,
                        request,
                        result,
                        lifecycleCorrelationId,
                        startedLifecycleEvent.EventId,
                        previousStatus: "started",
                        currentStatus: result.Success ? "succeeded" : "failed",
                        reason: result.FailureReason,
                        metadata: new Dictionary<string, string>
                        {
                            ["strategyName"] = strategy.GetType().Name,
                            ["retryable"] = result.Retryable.ToString()
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await this.RecordHostCreationEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        new Dictionary<string, object?>
                        {
                            [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                            [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = request.ProviderName,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = request.TransportName,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = request.TransportEndpoint,
                            [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                            [AiObservabilityMetadataKeys.DurationMs] = durationMs,
                            [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName,
                            [AiExceptionMetadataKeys.ExceptionMessage] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                await this.AppendHostLifecycleEventAsync(
                        AiRuntimeLifecycleEventType.HostCreationFailed,
                        request,
                        result: null,
                        lifecycleCorrelationId,
                        startedLifecycleEvent.EventId,
                        previousStatus: "started",
                        currentStatus: "failed",
                        reason: exception.Message,
                        metadata: new Dictionary<string, string>
                        {
                            ["strategyName"] = strategy.GetType().Name,
                            ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                throw;
            }
        }

        /// <summary>
        /// Records a runtime host creation result.
        /// </summary>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The host start result.</param>
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordHostCreationResultAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
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
                : result.FailureReason;

            await this.RecordHostCreationEventAsync(
                    eventType,
                    request,
                    result,
                    outcome,
                    failureReason,
                    durationMs,
                    new Dictionary<string, object?>
                    {
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                        [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = result.ProviderName ?? request.ProviderName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = result.TransportName ?? request.TransportName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = result.TransportEndpoint ?? request.TransportEndpoint,
                        [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                        ["success"] = result.Success,
                        [AiObservabilityMetadataKeys.FailureReason] = result.FailureReason,
                        [AiObservabilityMetadataKeys.DurationMs] = durationMs
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a runtime host creation control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The optional host start result.</param>
        /// <param name="outcome">The optional operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordHostCreationEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result,
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
                            Operation = RuntimeHostCreationOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.RuntimeInstanceId)
                                    ? Guid.NewGuid().ToString("N")
                                    : request.RuntimeInstanceId,
                                RuntimeInstanceId = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                PipelineKey = request.ExecutionContextSnapshot?.ContextKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                    [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = result?.ProviderName ?? request.ProviderName,
                                    [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = result?.TransportName ?? request.TransportName,
                                    [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = result?.TransportEndpoint ?? request.TransportEndpoint,
                                    [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.ExecutionContextSnapshot?.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.ExecutionContextSnapshot?.TenantGroupId,
                                    [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.ExecutionContextSnapshot?.ContextKey,
                                    ["success"] = result?.Success,
                                    [AiObservabilityMetadataKeys.FailureReason] = result?.FailureReason
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break host creation.
            }
        }

        /// <summary>
        /// Appends one durable host lifecycle event.
        /// </summary>
        private async Task<AiRuntimeLifecycleEvent> AppendHostLifecycleEventAsync(
            string eventType,
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result,
            string correlationId,
            string? causationId,
            string? previousStatus,
            string? currentStatus,
            string? reason,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            var isSharedInfrastructure =
                request.HostCreationMode == AiRuntimeHostCreationMode.KubernetesPool ||
                !string.IsNullOrWhiteSpace(request.PoolId);
            var isKubernetesHost =
                request.HostCreationMode is
                    AiRuntimeHostCreationMode.Kubernetes or
                    AiRuntimeHostCreationMode.KubernetesPool;
            var hostId = ResolveHostId(request, result);

            var eventId = CreateHostLifecycleEventId(
                eventType,
                request.ControlPlaneId,
                correlationId);
            var existing = await this.lifecycleJournal
                .GetByEventIdAsync(eventId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return existing;
            }

            var timestampUtc = DateTimeOffset.UtcNow;

            if (!string.IsNullOrWhiteSpace(causationId))
            {
                var causationEvent = await this.lifecycleJournal
                    .GetByEventIdAsync(causationId, cancellationToken)
                    .ConfigureAwait(false);

                if (causationEvent is not null &&
                    timestampUtc <= causationEvent.TimestampUtc)
                {
                    timestampUtc = causationEvent.TimestampUtc.AddTicks(1);
                }
            }

            var lifecycleEvent = new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = request.ControlPlaneId,
                HostCreationMode = request.HostCreationMode,
                ProviderName = result?.ProviderName ?? request.ProviderName,
                PoolId = request.PoolId,
                HostId = hostId,
                KubernetesPodUid = isKubernetesHost ? hostId : null,
                KubernetesNamespace = isKubernetesHost
                    ? ResolveMetadataValue(
                        request,
                        result,
                        AiKubernetesRuntimeHostMetadataKeys.Namespace)
                    : null,
                KubernetesPodName = isKubernetesHost
                    ? ResolveMetadataValue(
                        request,
                        result,
                        AiKubernetesRuntimeHostMetadataKeys.PodName)
                    : null,
                KubernetesNodeName = isKubernetesHost
                    ? ResolveMetadataValue(
                        request,
                        result,
                        AiKubernetesRuntimeHostMetadataKeys.NodeName)
                    : null,
                RuntimeInstanceId = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                TenantId = isSharedInfrastructure
                    ? null
                    : request.TenantId ?? request.ExecutionContextSnapshot.TenantId,
                TenantGroupId = isSharedInfrastructure
                    ? null
                    : request.TenantGroupId ?? request.ExecutionContextSnapshot.TenantGroupId,
                CorrelationId = correlationId,
                CausationId = causationId,
                PreviousStatus = previousStatus,
                CurrentStatus = currentStatus,
                Reason = reason,
                Metadata = MergeLifecycleMetadata(
                    metadata,
                    result?.TransportName ?? request.TransportName,
                    result?.TransportEndpoint ?? request.TransportEndpoint)
            };

            await this.lifecycleJournal
                .AppendAsync(lifecycleEvent, cancellationToken)
                .ConfigureAwait(false);

            return lifecycleEvent;
        }

        /// <summary>
        /// Creates the stable event identifier for one host creation transition.
        /// </summary>
        private static string CreateHostLifecycleEventId(
            string eventType,
            string controlPlaneId,
            string correlationId)
        {
            return $"{eventType}:{controlPlaneId}:{correlationId}";
        }

        /// <summary>
        /// Resolves the causal correlation identifier for one host creation attempt.
        /// </summary>
        private static string ResolveLifecycleCorrelationId(
            AiRuntimeHostStartRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.RequestId))
            {
                return request.RequestId;
            }

            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Propagates the host creation correlation to the runtime-only child without turning
        /// metadata into an authority for identity, routing, membership, or recovery.
        /// </summary>
        private static IReadOnlyDictionary<string, string> MergeLifecycleRequestMetadata(
            IReadOnlyDictionary<string, string> metadata,
            string correlationId,
            AiRuntimeHostCreationMode hostCreationMode)
        {
            var merged = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeHostMetadataKeys.LifecycleCorrelationId] = correlationId,
                [AiRuntimeHostMetadataKeys.HostCreationMode] = hostCreationMode.ToString()
            };

            return merged;
        }

        /// <summary>
        /// Resolves the exact host incarnation returned by the strategy.
        /// </summary>
        private static string? ResolveHostId(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result)
        {
            if (!string.IsNullOrWhiteSpace(request.HostId))
            {
                return request.HostId;
            }

            if (result?.Metadata.TryGetValue(AiRuntimeHostMetadataKeys.HostId, out var hostId) == true &&
                !string.IsNullOrWhiteSpace(hostId))
            {
                return hostId;
            }

            return null;
        }

        /// <summary>
        /// Resolves provider-enriched metadata without using it as lifecycle authority.
        /// </summary>
        private static string? ResolveMetadataValue(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result,
            string key)
        {
            if (result?.Metadata.TryGetValue(key, out var resultValue) == true &&
                !string.IsNullOrWhiteSpace(resultValue))
            {
                return resultValue;
            }

            return request.Metadata.TryGetValue(key, out var requestValue) &&
                   !string.IsNullOrWhiteSpace(requestValue)
                ? requestValue
                : null;
        }

        /// <summary>
        /// Builds non-authoritative transport diagnostics for a lifecycle event.
        /// </summary>
        private static IReadOnlyDictionary<string, string> MergeLifecycleMetadata(
            IReadOnlyDictionary<string, string>? metadata,
            string? transportName,
            string? transportEndpoint)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (metadata is not null)
            {
                foreach (var item in metadata)
                {
                    merged[item.Key] = item.Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(transportName))
            {
                merged[AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = transportName;
            }

            if (!string.IsNullOrWhiteSpace(transportEndpoint))
            {
                merged[AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = transportEndpoint;
            }

            return merged;
        }

        /// <summary>
        /// Resolves the operation outcome from a host start result.
        /// </summary>
        /// <param name="result">The host start result.</param>
        /// <returns>The operation outcome.</returns>
        private static AiControlPlaneOperationOutcome ResolveOutcome(
            AiRuntimeHostStartResult result)
        {
            return result.Success
                ? AiControlPlaneOperationOutcome.Succeeded
                : AiControlPlaneOperationOutcome.Denied;
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
