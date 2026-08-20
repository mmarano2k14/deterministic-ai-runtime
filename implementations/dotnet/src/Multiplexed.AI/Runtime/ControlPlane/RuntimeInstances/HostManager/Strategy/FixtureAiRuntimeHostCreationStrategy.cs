using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Execution;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy
{
    /// <summary>
    /// Fixture host creation strategy used by integration tests.
    /// </summary>
    public sealed class FixtureAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        private const string FixtureHostCreationOperation = "runtime-fixture-host-creation";

        /// <summary>
        /// The runtime instance registry used to publish the fixture runtime host registration.
        /// </summary>
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;

        /// <summary>
        /// The runtime instance capacity store used to publish the fixture runtime host capacity.
        /// </summary>
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;

        /// <summary>
        /// The control-plane observer.
        /// </summary>
        private readonly IAiControlPlaneObserver observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="FixtureAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        public FixtureAiRuntimeHostCreationStrategy(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore)
            : this(
                runtimeInstanceRegistry,
                runtimeInstanceCapacityStore,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FixtureAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        /// <param name="observer">The control-plane observer.</param>
        public FixtureAiRuntimeHostCreationStrategy(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore,
            IAiControlPlaneObserver observer)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.runtimeInstanceCapacityStore = runtimeInstanceCapacityStore ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStore));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordFixtureHostCreationEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                        [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                        [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = request.ProviderName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = request.TransportName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = request.TransportEndpoint,
                        [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
                        [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.ExecutionContextSnapshot?.ContextKey
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var metadata = CreateMetadata(request);
                var now = DateTimeOffset.UtcNow;

                await this.runtimeInstanceRegistry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ControlPlaneId = request.ControlPlaneId,
                        WorkerCount = request.WorkerCountPerInstance,
                        MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                        QueueCapacity = request.LocalQueueCapacity,
                        Metadata = metadata,
                        RegisteredAtUtc = now
                    },
                    cancellationToken).ConfigureAwait(false);

                await this.runtimeInstanceCapacityStore.PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Status = AiRuntimeInstanceStatus.Ready,
                        WorkerCount = request.WorkerCountPerInstance,
                        ActiveWorkerCount = 0,
                        AvailableWorkerCount = request.WorkerCountPerInstance,
                        MaxConcurrentRuns = request.MaxConcurrentRunsPerInstance,
                        MaxRunSlots = request.MaxConcurrentRunsPerInstance,
                        AvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                        ReservedRunSlots = 0,
                        EffectiveAvailableRunSlots = request.MaxConcurrentRunsPerInstance,
                        QueuedRunCount = 0,
                        RunningRunCount = 0,
                        ActiveRunCount = 0,
                        IsQueuePaused = false,
                        CanAcceptRun = true,
                        LastHeartbeatAtUtc = now,
                        ControlPlaneId = request.ControlPlaneId,
                        Metadata = metadata
                    },
                    cancellationToken).ConfigureAwait(false);

                var result = AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    metadata);

                await this.RecordFixtureHostCreationResultAsync(
                        request,
                        result,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return result;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await this.RecordFixtureHostCreationEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        null,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        durationMs,
                        new Dictionary<string, object?>
                        {
                            [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId,
                            [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
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

                throw;
            }
        }

        /// <summary>
        /// Records a fixture host creation result.
        /// </summary>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The host start result.</param>
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordFixtureHostCreationResultAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

            await this.RecordFixtureHostCreationEventAsync(
                    AiControlPlaneEventType.OperationCompleted,
                    request,
                    result,
                    AiControlPlaneOperationOutcome.Succeeded,
                    null,
                    durationMs,
                    new Dictionary<string, object?>
                    {
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                        [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                        [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = result.ProviderName ?? request.ProviderName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = result.TransportName ?? request.TransportName,
                        [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = result.TransportEndpoint ?? request.TransportEndpoint,
                        [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                        ["success"] = result.Success,
                        [AiObservabilityMetadataKeys.DurationMs] = durationMs
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Records a fixture host creation control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The host start request.</param>
        /// <param name="result">The optional host start result.</param>
        /// <param name="outcome">The optional outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="durationMs">The optional duration in milliseconds.</param>
        /// <param name="properties">The event properties.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RecordFixtureHostCreationEventAsync(
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
                            Operation = FixtureHostCreationOperation,
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
                                    [AiControlPlaneMetadataKeys.ControlPlaneId] = request.ControlPlaneId,
                                    [AiRuntimeInstanceProviderMetadataKeys.CamelCaseProviderName] = result?.ProviderName ?? request.ProviderName,
                                    [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName] = result?.TransportName ?? request.TransportName,
                                    [AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = result?.TransportEndpoint ?? request.TransportEndpoint,
                                    [AiRuntimeHostMetadataKeys.CamelCaseHostCreationMode] = request.HostCreationMode.ToString(),
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantGroupId] = request.TenantGroupId,
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
                // Control-plane observability must not break fixture host creation.
            }
        }

        /// <summary>
        /// Creates metadata for the fixture runtime host registration and capacity descriptors.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiRuntimeHostStartRequest request)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = request.ProviderName,
                [AiRuntimeInstanceMetadataKeys.Status] = AiRuntimeInstanceStatus.Ready.ToString(),
                [AiRuntimeHostMetadataKeys.LegacyHostCreationMode] = request.HostCreationMode.ToString(),
                ["hostCreation.strategy"] = nameof(FixtureAiRuntimeHostCreationStrategy),
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode,
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = request.PreferDedicatedCapacity.ToString(),
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = request.AllowSharedFallback.ToString(),
                [AiRuntimeInstanceProvisioningMetadataKeys.MaxRuntimeInstances] = request.MaxRuntimeInstances?.ToString() ?? string.Empty,
                [AiRuntimeInstanceProvisioningMetadataKeys.RuntimeInstanceIdPrefix] = request.RuntimeInstanceIdPrefix,
                [AiRuntimeInstanceProvisioningMetadataKeys.WorkerCountPerInstance] = request.WorkerCountPerInstance.ToString(),
                [AiRuntimeInstanceProvisioningMetadataKeys.MaxConcurrentRunsPerInstance] = request.MaxConcurrentRunsPerInstance.ToString(),
                [AiRuntimeInstanceProvisioningMetadataKeys.LocalQueueCapacity] = request.LocalQueueCapacity.ToString()
            };

            if (!string.IsNullOrWhiteSpace(request.TransportName))
            {
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = request.TransportName;
            }

            if (!string.IsNullOrWhiteSpace(request.TransportEndpoint))
            {
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = request.TransportEndpoint;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
            }

            return metadata;
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