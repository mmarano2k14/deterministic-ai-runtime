using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;

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
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
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
                        ["requestedBy"] = request.RequestedBy,
                        ["reason"] = request.Reason,
                        ["runtimeInstanceId"] = request.RuntimeInstanceId ?? request.Registration?.RuntimeInstanceId,
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
                        ["requestedBy"] = request.RequestedBy,
                        ["runtimeInstanceId"] =
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
                        ["requestedBy"] = request?.RequestedBy,
                        ["runtimeInstanceId"] = request?.RuntimeInstanceId ?? request?.Registration?.RuntimeInstanceId,
                        ["exceptionType"] = exception.GetType().Name
                    }
                },
                cancellationToken).ConfigureAwait(false);
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