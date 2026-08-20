using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.ControlPlane;


namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Runtime implementation of the runtime instance control-plane facade.
    ///
    /// This class wraps the runtime instance registry and exposes adapter-neutral
    /// registration, heartbeat, lookup, listing, draining, and unregister operations.
    ///
    /// Important:
    /// This class does not create Kubernetes pods, scale deployments, execute DAG steps,
    /// claim work, or replace local runtime queues.
    /// </summary>
    public sealed class AiRuntimeInstanceControlPlane : IAiRuntimeInstanceControlPlane
    {
        private readonly IAiRuntimeInstanceRegistry _registry;
        private readonly AiRuntimeInstanceControlPlaneOptions _options;
        private readonly IAiControlPlaneObserver _observer;
        private readonly IAiRuntimeLifecycleJournal _lifecycleJournal;
        private readonly ConcurrentDictionary<string, byte> _readyEventClaims = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceControlPlane"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="options">The runtime instance control-plane options.</param>
        /// <param name="observer">The control-plane observer used to record operation events.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="registry"/>, <paramref name="options"/>, or <paramref name="observer"/> is null.
        /// </exception>
        public AiRuntimeInstanceControlPlane(
            IAiRuntimeInstanceRegistry registry,
            IOptions<AiRuntimeInstanceControlPlaneOptions> options,
            IAiControlPlaneObserver observer)
            : this(
                registry,
                options,
                observer,
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceControlPlane"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="options">The runtime instance control-plane options.</param>
        /// <param name="observer">The control-plane observer used to record operation events.</param>
        /// <param name="lifecycleJournal">The runtime infrastructure lifecycle journal.</param>
        /// <exception cref="ArgumentNullException">Thrown when a required dependency is null.</exception>
        public AiRuntimeInstanceControlPlane(
            IAiRuntimeInstanceRegistry registry,
            IOptions<AiRuntimeInstanceControlPlaneOptions> options,
            IAiControlPlaneObserver observer,
            IAiRuntimeLifecycleJournal lifecycleJournal)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
            _lifecycleJournal = lifecycleJournal ?? throw new ArgumentNullException(nameof(lifecycleJournal));
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> ExecuteAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return request.Operation switch
            {
                AiRuntimeInstanceControlPlaneOperation.Register => RegisterAsync(request, cancellationToken),
                AiRuntimeInstanceControlPlaneOperation.Heartbeat => HeartbeatAsync(request, cancellationToken),
                AiRuntimeInstanceControlPlaneOperation.GetInstance => GetInstanceAsync(request, cancellationToken),
                AiRuntimeInstanceControlPlaneOperation.ListInstances => ListInstancesAsync(request, cancellationToken),
                AiRuntimeInstanceControlPlaneOperation.MarkDraining => MarkDrainingAsync(request, cancellationToken),
                AiRuntimeInstanceControlPlaneOperation.Unregister => UnregisterAsync(request, cancellationToken),

                _ => throw new NotSupportedException(
                    $"Runtime instance control-plane operation '{request.Operation}' is not supported.")
            };
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> RegisterAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.Register,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> HeartbeatAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.Heartbeat,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> GetInstanceAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.GetInstance,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> ListInstancesAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.ListInstances,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> MarkDrainingAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.MarkDraining,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceControlPlaneResult> UnregisterAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteInstanceOperationAsync(
                request,
                AiRuntimeInstanceControlPlaneOperation.Unregister,
                cancellationToken);
        }

        /// <summary>
        /// Executes one runtime instance control-plane operation with validation,
        /// observability events, duration measurement, and structured failure handling.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="operation">The runtime instance control-plane operation to execute.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance control-plane result.</returns>
        private async Task<AiRuntimeInstanceControlPlaneResult> ExecuteInstanceOperationAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            AiRuntimeInstanceControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var correlation = CreateCorrelation(request);

            try
            {
                ValidateRequest(request, operation);
                EnsureEnabled(operation);

                await RecordStartedAsync(
                    request,
                    operation,
                    correlation,
                    cancellationToken).ConfigureAwait(false);

                var operationResult = await ExecuteInnerAsync(
                    request,
                    operation,
                    cancellationToken).ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordCompletedAsync(
                    request,
                    operation,
                    correlation,
                    operationResult,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                await RecordLifecycleEventsAsync(
                    operation,
                    operationResult.Instance,
                    cancellationToken).ConfigureAwait(false);

                return new AiRuntimeInstanceControlPlaneResult
                {
                    Operation = operation,
                    Success = true,
                    Message = $"Runtime instance control-plane operation '{operation}' completed successfully.",
                    RuntimeInstanceId =
                        operationResult.Instance?.RuntimeInstanceId ??
                        request.RuntimeInstanceId ??
                        request.Registration?.RuntimeInstanceId,
                    Instance = operationResult.Instance,
                    Instances = operationResult.Instances,
                    CorrelationId = correlation.CorrelationId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs
                };
            }
            catch (Exception exception) when (_options.ReturnFailureResultInsteadOfThrowing)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordFailedAsync(
                    request,
                    operation,
                    correlation,
                    exception,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                return new AiRuntimeInstanceControlPlaneResult
                {
                    Operation = operation,
                    Success = false,
                    Message = $"Runtime instance control-plane operation '{operation}' failed.",
                    RuntimeInstanceId = request?.RuntimeInstanceId ?? request?.Registration?.RuntimeInstanceId,
                    Diagnostics = request?.IncludeDiagnostics == true
                        ? new[] { exception.Message }
                        : Array.Empty<string>(),
                    CorrelationId = correlation.CorrelationId,
                    RequestedBy = request?.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    FailureReason = exception.Message
                };
            }
        }

        /// <summary>
        /// Executes the inner registry operation and returns the raw operation result.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="operation">The runtime instance control-plane operation to execute.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The inner runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> ExecuteInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            AiRuntimeInstanceControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            return operation switch
            {
                AiRuntimeInstanceControlPlaneOperation.Register =>
                    await RegisterInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeInstanceControlPlaneOperation.Heartbeat =>
                    await HeartbeatInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeInstanceControlPlaneOperation.GetInstance =>
                    await GetInstanceInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeInstanceControlPlaneOperation.ListInstances =>
                    await ListInstancesInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeInstanceControlPlaneOperation.MarkDraining =>
                    await MarkDrainingInnerAsync(request, cancellationToken).ConfigureAwait(false),

                AiRuntimeInstanceControlPlaneOperation.Unregister =>
                    await UnregisterInnerAsync(request, cancellationToken).ConfigureAwait(false),

                _ => throw new NotSupportedException(
                    $"Runtime instance control-plane operation '{operation}' is not supported.")
            };
        }

        /// <summary>
        /// Registers or updates a runtime instance.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> RegisterInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instance = await _registry
                .RegisterAsync(request.Registration!, cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instance = instance
            };
        }

        /// <summary>
        /// Records a runtime instance heartbeat and updates its queue/run/worker visibility state.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> HeartbeatInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instance = await _registry
                .HeartbeatAsync(
                    request.RuntimeInstanceId!,
                    request.QueuedRunCount,
                    request.RunningRunCount,
                    request.ActiveRunCount,
                    request.AvailableRunSlots,
                    request.ActiveWorkerCount,
                    request.AvailableWorkerCount,
                    request.MaxLocalWorkersPerExecution,
                    request.IsQueuePaused,
                    request.CanAcceptRun,
                    request.Status,
                    cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instance = instance
            };
        }

        /// <summary>
        /// Gets a registered runtime instance snapshot.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> GetInstanceInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instance = await _registry
                .GetAsync(request.RuntimeInstanceId!, cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instance = instance
            };
        }

        /// <summary>
        /// Lists registered runtime instance snapshots.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> ListInstancesInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instances = await _registry
                .ListAsync(request.IncludeStopped, cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instances = instances
            };
        }

        /// <summary>
        /// Marks a runtime instance as draining.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> MarkDrainingInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instance = await _registry
                .MarkDrainingAsync(request.RuntimeInstanceId!, cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instance = instance
            };
        }

        /// <summary>
        /// Unregisters a runtime instance by marking it as stopped.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance operation result.</returns>
        private async Task<RuntimeInstanceOperationResult> UnregisterInnerAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            CancellationToken cancellationToken)
        {
            var instance = await _registry
                .UnregisterAsync(request.RuntimeInstanceId!, cancellationToken)
                .ConfigureAwait(false);

            return new RuntimeInstanceOperationResult
            {
                Instance = instance
            };
        }

        /// <summary>
        /// Creates a runtime correlation context for runtime instance control-plane observability.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <returns>The runtime execution correlation context.</returns>
        private static AiRuntimeExecutionCorrelationContext CreateCorrelation(
            AiRuntimeInstanceControlPlaneRequest request)
        {
            var runtimeInstanceId =
                request.RuntimeInstanceId ??
                request.Registration?.RuntimeInstanceId;

            return new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId,

                RuntimeInstanceId = runtimeInstanceId
            };
        }

        /// <summary>
        /// Records a control-plane operation started event.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="operation">The operation being started.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RecordStartedAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            AiRuntimeInstanceControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationStarted,
                    Area = AiControlPlaneArea.InstanceRegistry,
                    Operation = operation.ToString(),
                    Correlation = correlation,
                    Message = $"Runtime instance control-plane operation '{operation}' started.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                        ["reason"] = request.Reason,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.RuntimeInstanceId ?? request.Registration?.RuntimeInstanceId,
                        ["includeStopped"] = request.IncludeStopped,
                        ["status"] = request.Status.ToString()
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation completed event.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="operation">The completed operation.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="operationResult">The inner runtime instance operation result.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RecordCompletedAsync(
            AiRuntimeInstanceControlPlaneRequest request,
            AiRuntimeInstanceControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            RuntimeInstanceOperationResult operationResult,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationCompleted,
                    Area = AiControlPlaneArea.InstanceRegistry,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Succeeded,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Runtime instance control-plane operation '{operation}' completed successfully.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] =
                            operationResult.Instance?.RuntimeInstanceId ??
                            request.RuntimeInstanceId ??
                            request.Registration?.RuntimeInstanceId,
                        ["instanceCount"] = operationResult.Instances.Count,
                        ["status"] = operationResult.Instance?.Status.ToString()
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records a control-plane operation failed event.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request, when available.</param>
        /// <param name="operation">The failed operation.</param>
        /// <param name="correlation">The runtime correlation context.</param>
        /// <param name="exception">The exception that caused the failure.</param>
        /// <param name="durationMs">The operation duration in milliseconds.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        private async Task RecordFailedAsync(
            AiRuntimeInstanceControlPlaneRequest? request,
            AiRuntimeInstanceControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            Exception exception,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationFailed,
                    Area = AiControlPlaneArea.InstanceRegistry,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Failed,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Runtime instance control-plane operation '{operation}' failed.",
                    FailureReason = exception.Message,
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request?.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request?.RequestedBy,
                        [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request?.RuntimeInstanceId ?? request?.Registration?.RuntimeInstanceId,
                        ["exceptionType"] = exception.GetType().Name
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Records durable lifecycle events owned by the common runtime registry facade.
        /// </summary>
        private async Task RecordLifecycleEventsAsync(
            AiRuntimeInstanceControlPlaneOperation operation,
            AiRuntimeInstanceSnapshot? instance,
            CancellationToken cancellationToken)
        {
            if (instance is null)
            {
                return;
            }

            var lifecycleCorrelationId = ResolveRuntimeLifecycleCorrelationId(instance);
            string? registeredEventId = null;

            if (operation == AiRuntimeInstanceControlPlaneOperation.Register)
            {
                registeredEventId = CreateRegisteredEventId(instance);
                var existingRegisteredEvent = await _lifecycleJournal
                    .GetByEventIdAsync(registeredEventId, cancellationToken)
                    .ConfigureAwait(false);

                if (existingRegisteredEvent is null)
                {
                    await _lifecycleJournal.AppendAsync(
                        CreateRuntimeLifecycleEvent(
                            registeredEventId,
                            AiRuntimeLifecycleEventType.RuntimeRegistered,
                            ResolveRegisteredTimestampUtc(instance),
                            instance,
                            lifecycleCorrelationId,
                            causationId: null,
                            previousStatus: null,
                            currentStatus: "registered"),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (instance.Status == AiRuntimeInstanceStatus.Ready &&
                operation is AiRuntimeInstanceControlPlaneOperation.Register or
                    AiRuntimeInstanceControlPlaneOperation.Heartbeat)
            {
                await AppendRuntimeReadyOnceAsync(
                    instance,
                    lifecycleCorrelationId,
                    registeredEventId ?? CreateRegisteredEventId(instance),
                    operation == AiRuntimeInstanceControlPlaneOperation.Register
                        ? "registered"
                        : null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation == AiRuntimeInstanceControlPlaneOperation.MarkDraining &&
                instance.Status == AiRuntimeInstanceStatus.Draining)
            {
                await AppendRuntimeStatusOnceAsync(
                    instance,
                    AiRuntimeLifecycleEventType.RuntimeDraining,
                    lifecycleCorrelationId,
                    CreateReadyEventId(instance),
                    "ready",
                    AiRuntimeInstanceStatus.Draining.ToString(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation == AiRuntimeInstanceControlPlaneOperation.Unregister &&
                instance.Status == AiRuntimeInstanceStatus.Stopped)
            {
                await AppendRuntimeStatusOnceAsync(
                    instance,
                    AiRuntimeLifecycleEventType.RuntimeStopped,
                    lifecycleCorrelationId,
                    causationId: null,
                    previousStatus: null,
                    currentStatus: AiRuntimeInstanceStatus.Stopped.ToString(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task AppendRuntimeStatusOnceAsync(
            AiRuntimeInstanceSnapshot instance,
            string eventType,
            string correlationId,
            string? causationId,
            string? previousStatus,
            string currentStatus,
            CancellationToken cancellationToken)
        {
            var eventId = CreateRuntimeStatusEventId(eventType, instance);
            var existing = await _lifecycleJournal
                .GetByEventIdAsync(eventId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return;
            }

            await _lifecycleJournal.AppendAsync(
                CreateRuntimeLifecycleEvent(
                    eventId,
                    eventType,
                    DateTimeOffset.UtcNow,
                    instance,
                    correlationId,
                    causationId,
                    previousStatus,
                    currentStatus),
                cancellationToken).ConfigureAwait(false);
        }

        private static string CreateRuntimeStatusEventId(
            string eventType,
            AiRuntimeInstanceSnapshot instance)
        {
            return string.Join(
                ":",
                eventType,
                instance.ControlPlaneId,
                instance.RuntimeInstanceId,
                ResolveRegisteredTimestampUtc(instance).UtcDateTime.Ticks);
        }

        /// <summary>
        /// Appends one durable ready transition for one runtime registration incarnation.
        /// </summary>
        private async Task AppendRuntimeReadyOnceAsync(
            AiRuntimeInstanceSnapshot instance,
            string correlationId,
            string causationId,
            string? previousStatus,
            CancellationToken cancellationToken)
        {
            var eventId = CreateReadyEventId(instance);

            if (!_readyEventClaims.TryAdd(eventId, 0))
            {
                return;
            }

            try
            {
                var existing = await _lifecycleJournal
                    .GetByEventIdAsync(eventId, cancellationToken)
                    .ConfigureAwait(false);

                if (existing is not null)
                {
                    return;
                }

                await _lifecycleJournal.AppendAsync(
                    CreateRuntimeLifecycleEvent(
                        eventId,
                        AiRuntimeLifecycleEventType.RuntimeReady,
                        ResolveReadyTimestampUtc(instance),
                        instance,
                        correlationId,
                        causationId,
                        previousStatus,
                        AiRuntimeInstanceStatus.Ready.ToString()),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _readyEventClaims.TryRemove(eventId, out _);
                throw;
            }
        }

        /// <summary>
        /// Creates one strongly typed runtime lifecycle event from a registry snapshot.
        /// </summary>
        private static AiRuntimeLifecycleEvent CreateRuntimeLifecycleEvent(
            string eventId,
            string eventType,
            DateTimeOffset timestampUtc,
            AiRuntimeInstanceSnapshot instance,
            string correlationId,
            string? causationId,
            string? previousStatus,
            string? currentStatus)
        {
            var hostCreationMode = ResolveHostCreationMode(instance);
            var isKubernetes =
                hostCreationMode is AiRuntimeHostCreationMode.Kubernetes or
                    AiRuntimeHostCreationMode.KubernetesPool ||
                !string.IsNullOrWhiteSpace(instance.KubernetesNamespace) ||
                !string.IsNullOrWhiteSpace(instance.KubernetesPodName) ||
                !string.IsNullOrWhiteSpace(instance.KubernetesNodeName);
            var isSharedInfrastructure = !string.IsNullOrWhiteSpace(instance.PoolId);

            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = instance.ControlPlaneId ?? string.Empty,
                HostCreationMode = hostCreationMode,
                ProviderName = instance.Metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName)
                        ? providerName
                        : null,
                PoolId = instance.PoolId,
                HostId = instance.HostId,
                KubernetesPodUid = isKubernetes ? instance.HostId : null,
                KubernetesNamespace = instance.KubernetesNamespace,
                KubernetesPodName = instance.KubernetesPodName,
                KubernetesNodeName = instance.KubernetesNodeName,
                RuntimeInstanceId = instance.RuntimeInstanceId,
                RuntimeId = instance.RuntimeId,
                ProcessId = instance.ProcessId,
                TenantId = isSharedInfrastructure ? null : instance.TenantId,
                TenantGroupId = isSharedInfrastructure ? null : instance.TenantGroupId,
                CorrelationId = correlationId,
                CausationId = causationId,
                PreviousStatus = previousStatus,
                CurrentStatus = currentStatus,
                Metadata = CreateRuntimeLifecycleMetadata(instance)
            };
        }

        /// <summary>
        /// Resolves the physical host creation mode from strongly typed registry identity.
        /// </summary>
        private static AiRuntimeHostCreationMode? ResolveHostCreationMode(
            AiRuntimeInstanceSnapshot instance)
        {
            if (instance.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostCreationMode,
                    out var configuredMode) &&
                Enum.TryParse<AiRuntimeHostCreationMode>(
                    configuredMode,
                    ignoreCase: true,
                    out var hostCreationMode))
            {
                return hostCreationMode;
            }

            var isKubernetes =
                !string.IsNullOrWhiteSpace(instance.KubernetesNamespace) ||
                !string.IsNullOrWhiteSpace(instance.KubernetesPodName) ||
                !string.IsNullOrWhiteSpace(instance.KubernetesNodeName);

            if (isKubernetes)
            {
                return string.IsNullOrWhiteSpace(instance.PoolId)
                    ? AiRuntimeHostCreationMode.Kubernetes
                    : AiRuntimeHostCreationMode.KubernetesPool;
            }

            return instance.ProcessId.HasValue
                ? AiRuntimeHostCreationMode.Process
                : null;
        }

        /// <summary>
        /// Resolves the stable host-creation correlation propagated to this runtime incarnation.
        /// </summary>
        private static string ResolveRuntimeLifecycleCorrelationId(
            AiRuntimeInstanceSnapshot instance)
        {
            if (instance.Metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.LifecycleCorrelationId,
                    out var correlationId) &&
                !string.IsNullOrWhiteSpace(correlationId))
            {
                return correlationId;
            }

            return instance.HostId ??
                $"{instance.ControlPlaneId}:{instance.RuntimeInstanceId}:{ResolveRegisteredTimestampUtc(instance).UtcDateTime.Ticks}";
        }

        /// <summary>
        /// Resolves the durable registration timestamp for one runtime incarnation.
        /// </summary>
        private static DateTimeOffset ResolveRegisteredTimestampUtc(
            AiRuntimeInstanceSnapshot instance)
        {
            return instance.RegisteredAtUtc == default
                ? instance.LastHeartbeatAtUtc
                : instance.RegisteredAtUtc;
        }

        /// <summary>
        /// Resolves a deterministic timestamp for the first ready state observed on the incarnation.
        /// </summary>
        private static DateTimeOffset ResolveReadyTimestampUtc(
            AiRuntimeInstanceSnapshot instance)
        {
            var registeredAtUtc = ResolveRegisteredTimestampUtc(instance);
            var readyAtUtc = DateTimeOffset.UtcNow;

            return readyAtUtc > registeredAtUtc
                ? readyAtUtc
                : registeredAtUtc.AddTicks(1);
        }

        /// <summary>
        /// Creates compact non-authoritative diagnostics for a runtime lifecycle event.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CreateRuntimeLifecycleMetadata(
            AiRuntimeInstanceSnapshot instance)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(instance.HostName))
            {
                metadata["hostName"] = instance.HostName;
            }

            if (!string.IsNullOrWhiteSpace(instance.RuntimeVersion))
            {
                metadata["runtimeVersion"] = instance.RuntimeVersion;
            }

            return metadata;
        }

        /// <summary>
        /// Creates the stable registration event identifier for one runtime incarnation.
        /// </summary>
        private static string CreateRegisteredEventId(
            AiRuntimeInstanceSnapshot instance)
        {
            return $"runtime.registered:{instance.ControlPlaneId}:{instance.RuntimeInstanceId}:{ResolveRegisteredTimestampUtc(instance).UtcDateTime.Ticks}";
        }

        /// <summary>
        /// Creates the stable ready event identifier for one runtime incarnation.
        /// </summary>
        private static string CreateReadyEventId(
            AiRuntimeInstanceSnapshot instance)
        {
            return $"runtime.ready:{instance.ControlPlaneId}:{instance.RuntimeInstanceId}:{ResolveRegisteredTimestampUtc(instance).UtcDateTime.Ticks}";
        }

        /// <summary>
        /// Validates a runtime instance control-plane request for the specified operation.
        /// </summary>
        /// <param name="request">The runtime instance control-plane request.</param>
        /// <param name="operation">The operation being validated.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="request"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when required operation inputs are missing.
        /// </exception>
        private static void ValidateRequest(
            AiRuntimeInstanceControlPlaneRequest request,
            AiRuntimeInstanceControlPlaneOperation operation)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (operation == AiRuntimeInstanceControlPlaneOperation.Register &&
                request.Registration is null)
            {
                throw new ArgumentException(
                    "Registration is required for Register operations.",
                    nameof(request));
            }

            if (RequiresRuntimeInstanceId(operation) &&
                string.IsNullOrWhiteSpace(request.RuntimeInstanceId))
            {
                throw new ArgumentException(
                    "RuntimeInstanceId is required for this runtime instance control-plane operation.",
                    nameof(request));
            }
        }

        /// <summary>
        /// Determines whether the specified operation requires a runtime instance identifier.
        /// </summary>
        /// <param name="operation">The runtime instance control-plane operation.</param>
        /// <returns>
        /// <c>true</c> when the operation requires <see cref="AiRuntimeInstanceControlPlaneRequest.RuntimeInstanceId"/>;
        /// otherwise, <c>false</c>.
        /// </returns>
        private static bool RequiresRuntimeInstanceId(
            AiRuntimeInstanceControlPlaneOperation operation)
        {
            return operation is
                AiRuntimeInstanceControlPlaneOperation.Heartbeat or
                AiRuntimeInstanceControlPlaneOperation.GetInstance or
                AiRuntimeInstanceControlPlaneOperation.MarkDraining or
                AiRuntimeInstanceControlPlaneOperation.Unregister;
        }

        /// <summary>
        /// Ensures the specified runtime instance control-plane operation is enabled.
        /// </summary>
        /// <param name="operation">The runtime instance control-plane operation.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the operation is disabled by <see cref="AiRuntimeInstanceControlPlaneOptions"/>.
        /// </exception>
        private void EnsureEnabled(
            AiRuntimeInstanceControlPlaneOperation operation)
        {
            var enabled = operation switch
            {
                AiRuntimeInstanceControlPlaneOperation.Register => _options.EnableRegister,
                AiRuntimeInstanceControlPlaneOperation.Heartbeat => _options.EnableHeartbeat,
                AiRuntimeInstanceControlPlaneOperation.GetInstance => _options.EnableGetInstance,
                AiRuntimeInstanceControlPlaneOperation.ListInstances => _options.EnableListInstances,
                AiRuntimeInstanceControlPlaneOperation.MarkDraining => _options.EnableMarkDraining,
                AiRuntimeInstanceControlPlaneOperation.Unregister => _options.EnableUnregister,
                _ => false
            };

            if (!enabled)
            {
                throw new InvalidOperationException(
                    $"Runtime instance control-plane operation '{operation}' is disabled.");
            }
        }

        /// <summary>
        /// Calculates the control-plane operation duration in milliseconds.
        /// </summary>
        /// <param name="startedAtUtc">The operation start timestamp.</param>
        /// <param name="completedAtUtc">The operation completion timestamp.</param>
        /// <returns>
        /// The operation duration in milliseconds, or <c>0</c> when duration measurement is disabled.
        /// </returns>
        private long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            if (!_options.MeasureDuration)
            {
                return 0;
            }

            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Internal result produced by a runtime instance registry operation.
        /// </summary>
        private sealed class RuntimeInstanceOperationResult
        {
            /// <summary>
            /// Gets the runtime instance snapshot returned by single-instance operations.
            /// </summary>
            public AiRuntimeInstanceSnapshot? Instance { get; init; }

            /// <summary>
            /// Gets the runtime instance snapshots returned by list operations.
            /// </summary>
            public IReadOnlyList<AiRuntimeInstanceSnapshot> Instances { get; init; } =
                Array.Empty<AiRuntimeInstanceSnapshot>();
        }
    }
}
