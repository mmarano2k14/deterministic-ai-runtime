using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;


namespace Multiplexed.AI.Runtime.ControlPlane.SharedQueue
{
    /// <summary>
    /// Default implementation of the shared queue pump.
    /// </summary>
    /// <remarks>
    /// The pump executes one controlled dispatch cycle.
    ///
    /// It repeatedly calls <see cref="IAiSharedQueueDispatcher"/> until:
    /// - the maximum dispatch count is reached
    /// - no pending item is available
    /// - a dispatch failure occurs and options require stopping on failure
    /// - cancellation is requested
    ///
    /// This class is not a background service by itself.
    /// A hosted service, CLI command, API endpoint, MCP server, or runtime instance loop
    /// can call it.
    ///
    /// The pump does not build Redis keys directly.
    /// Redis scoping is owned by Redis-backed stores.
    /// The pump only transports metadata, including the logical control-plane identifier,
    /// to the dispatcher.
    /// </remarks>
    public sealed class AiSharedQueuePump : IAiSharedQueuePump
    {
        private const string SharedQueuePumpOperation = "shared-queue-pump-cycle";

        private readonly IAiSharedQueueDispatcher _dispatcher;
        private readonly AiSharedQueuePumpOptions _options;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly ILogger<AiSharedQueuePump> _logger;
        private readonly IAiControlPlaneObserver _observer;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueuePump"/> class.
        /// </summary>
        /// <param name="dispatcher">The shared queue dispatcher.</param>
        /// <param name="options">The pump options.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane identifier resolver.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dispatcher"/>, <paramref name="options"/>, or <paramref name="logger"/> is null.
        /// </exception>
        public AiSharedQueuePump(
            IAiSharedQueueDispatcher dispatcher,
            IOptions<AiSharedQueuePumpOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            ILogger<AiSharedQueuePump> logger)
            : this(
                dispatcher,
                options,
                controlPlaneIdResolver,
                logger,
                new NoopAiControlPlaneObserver())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueuePump"/> class.
        /// </summary>
        /// <param name="dispatcher">The shared queue dispatcher.</param>
        /// <param name="options">The pump options.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane identifier resolver.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dispatcher"/>, <paramref name="options"/>, <paramref name="logger"/>, or <paramref name="observer"/> is null.
        /// </exception>
        public AiSharedQueuePump(
            IAiSharedQueueDispatcher dispatcher,
            IOptions<AiSharedQueuePumpOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            ILogger<AiSharedQueuePump> logger,
            IAiControlPlaneObserver observer)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        /// <inheritdoc />
        public async Task<AiSharedQueuePumpResult> PumpOnceAsync(
            AiSharedQueuePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PumpRuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;
            var controlPlaneId =
                await ResolveControlPlaneIdAsync(
                        request.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            var controlPlaneMetadata =
                await _controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = controlPlaneId,
                            Metadata = request.Metadata,
                            Source = "shared-queue-pump-metadata",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            var dispatchMetadata =
                MergeMetadata(
                    request.Metadata,
                    controlPlaneMetadata);

            if (!_options.Enabled)
            {
                var disabledCompletedAtUtc = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "Shared queue pump skipped because it is disabled. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                    controlPlaneId,
                    request.PumpRuntimeInstanceId,
                    request.PumpWorkerId,
                    request.TenantId,
                    request.PipelineKey);

                await RecordSharedQueuePumpEventAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        request,
                        request.PumpWorkerId,
                        AiControlPlaneOperationOutcome.Denied,
                        "shared-queue-pump-disabled",
                        new Dictionary<string, object?>
                        {
                            [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId,
                            ["enabled"] = false,
                            [AiObservabilityMetadataKeys.DurationMs] = CalculateDurationMs(startedAtUtc, disabledCompletedAtUtc)
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new AiSharedQueuePumpResult
                {
                    Success = false,
                    RuntimeInstanceId = request.PumpRuntimeInstanceId,
                    FailureReason = "Shared queue pump is disabled.",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = disabledCompletedAtUtc,
                    DurationMs = CalculateDurationMs(startedAtUtc, disabledCompletedAtUtc),
                    Diagnostics = new[] { "Shared queue pump is disabled." }
                };
            }

            var maxDispatches = ResolveMaxDispatches(request);
            var claimTtl = ResolveClaimTtl(request);
            var workerId = ResolveWorkerId(request);
            var source = ResolveSource(request);

            await RecordSharedQueuePumpEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    workerId,
                    null,
                    null,
                    new Dictionary<string, object?>
                    {
                        [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId,
                        ["maxDispatches"] = maxDispatches,
                        ["claimTtlMs"] = claimTtl.TotalMilliseconds,
                        ["source"] = source,
                        ["reason"] = request.Reason
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Shared queue pump cycle started. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, MaxDispatches={MaxDispatches}, ClaimTtlMs={ClaimTtlMs}, Source={Source}, Reason={Reason}",
                controlPlaneId,
                request.PumpRuntimeInstanceId,
                workerId,
                request.TenantId,
                request.PipelineKey,
                maxDispatches,
                claimTtl.TotalMilliseconds,
                source,
                request.Reason);

            var dispatchResults = new List<AiSharedQueueDispatchResult>();
            var successfulDispatches = 0;
            var failedDispatches = 0;
            var stoppedBecauseNoItemAvailable = false;

            try
            {
                for (var index = 0; index < maxDispatches; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogDebug(
                        "Shared queue pump dispatch attempt started. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, MaxDispatches={MaxDispatches}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                        controlPlaneId,
                        request.PumpRuntimeInstanceId,
                        workerId,
                        index + 1,
                        maxDispatches,
                        request.TenantId,
                        request.PipelineKey);

                    var dispatchResult = await _dispatcher
                        .DispatchNextAsync(
                            new AiSharedQueueDispatchRequest
                            {
                                RuntimeInstanceId = request.PumpRuntimeInstanceId,
                                WorkerId = workerId,
                                TenantId = request.TenantId,
                                PipelineKey = request.PipelineKey,
                                ClaimTtl = claimTtl,
                                CorrelationId = request.CorrelationId,
                                RequestedBy = request.RequestedBy,
                                Source = source,
                                Reason = request.Reason ?? "Shared queue pump dispatch cycle.",
                                Metadata = dispatchMetadata
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    dispatchResults.Add(dispatchResult);

                    if (dispatchResult.NoItemAvailable)
                    {
                        stoppedBecauseNoItemAvailable = true;

                        _logger.LogDebug(
                            "Shared queue pump dispatch attempt found no item. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, TenantId={TenantId}, PipelineKey={PipelineKey}, StopCycleWhenNoItemAvailable={StopCycleWhenNoItemAvailable}",
                            controlPlaneId,
                            request.PumpRuntimeInstanceId,
                            workerId,
                            index + 1,
                            request.TenantId,
                            request.PipelineKey,
                            _options.StopCycleWhenNoItemAvailable);

                        if (_options.StopCycleWhenNoItemAvailable)
                        {
                            break;
                        }

                        continue;
                    }

                    if (dispatchResult.Success)
                    {
                        successfulDispatches++;

                        _logger.LogInformation(
                            "Shared queue pump dispatch attempt succeeded. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, TenantId={TenantId}, PipelineKey={PipelineKey}, Diagnostics={Diagnostics}",
                            controlPlaneId,
                            request.PumpRuntimeInstanceId,
                            workerId,
                            index + 1,
                            successfulDispatches,
                            failedDispatches,
                            request.TenantId,
                            request.PipelineKey,
                            string.Join(" | ", dispatchResult.Diagnostics ?? Array.Empty<string>()));

                        continue;
                    }

                    failedDispatches++;

                    _logger.LogWarning(
                        "Shared queue pump dispatch attempt failed. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, TenantId={TenantId}, PipelineKey={PipelineKey}, FailureReason={FailureReason}, Diagnostics={Diagnostics}, StopCycleOnDispatchFailure={StopCycleOnDispatchFailure}",
                        controlPlaneId,
                        request.PumpRuntimeInstanceId,
                        workerId,
                        index + 1,
                        successfulDispatches,
                        failedDispatches,
                        request.TenantId,
                        request.PipelineKey,
                        dispatchResult.FailureReason,
                        string.Join(" | ", dispatchResult.Diagnostics ?? Array.Empty<string>()),
                        _options.StopCycleOnDispatchFailure);

                    if (_options.StopCycleOnDispatchFailure)
                    {
                        break;
                    }
                }

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);
                var outcome = failedDispatches > 0
                    ? AiControlPlaneOperationOutcome.CompletedWithIssues
                    : AiControlPlaneOperationOutcome.Succeeded;

                await RecordSharedQueuePumpEventAsync(
                        AiControlPlaneEventType.OperationCompleted,
                        request,
                        workerId,
                        outcome,
                        failedDispatches > 0 ? "shared-queue-dispatch-failures-detected" : null,
                        new Dictionary<string, object?>
                        {
                            [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId,
                            ["attemptedDispatchCount"] = dispatchResults.Count,
                            ["successfulDispatchCount"] = successfulDispatches,
                            ["failedDispatchCount"] = failedDispatches,
                            ["stoppedBecauseNoItemAvailable"] = stoppedBecauseNoItemAvailable,
                            [AiObservabilityMetadataKeys.DurationMs] = durationMs,
                            ["maxDispatches"] = maxDispatches,
                            ["claimTtlMs"] = claimTtl.TotalMilliseconds,
                            ["source"] = source
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Shared queue pump cycle completed. Success=True, ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, Diagnostics={Diagnostics}",
                    controlPlaneId,
                    request.PumpRuntimeInstanceId,
                    workerId,
                    request.TenantId,
                    request.PipelineKey,
                    dispatchResults.Count,
                    successfulDispatches,
                    failedDispatches,
                    stoppedBecauseNoItemAvailable,
                    durationMs,
                    string.Join(" | ", BuildDiagnostics(dispatchResults)));

                return new AiSharedQueuePumpResult
                {
                    Success = true,
                    RuntimeInstanceId = request.PumpRuntimeInstanceId,
                    AttemptedDispatchCount = dispatchResults.Count,
                    SuccessfulDispatchCount = successfulDispatches,
                    FailedDispatchCount = failedDispatches,
                    StoppedBecauseNoItemAvailable = stoppedBecauseNoItemAvailable,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    DispatchResults = dispatchResults.ToArray(),
                    Diagnostics = BuildDiagnostics(dispatchResults)
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordSharedQueuePumpEventAsync(
                        AiControlPlaneEventType.OperationFailed,
                        request,
                        workerId,
                        AiControlPlaneOperationOutcome.Failed,
                        exception.GetType().Name,
                        new Dictionary<string, object?>
                        {
                            [AiControlPlaneMetadataKeys.ControlPlaneId] = controlPlaneId,
                            ["attemptedDispatchCount"] = dispatchResults.Count,
                            ["successfulDispatchCount"] = successfulDispatches,
                            ["failedDispatchCount"] = failedDispatches,
                            ["stoppedBecauseNoItemAvailable"] = stoppedBecauseNoItemAvailable,
                            [AiObservabilityMetadataKeys.DurationMs] = durationMs,
                            [AiExceptionMetadataKeys.ExceptionType] = exception.GetType().FullName,
                            [AiExceptionMetadataKeys.ExceptionMessage] = exception.Message
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogError(
                    exception,
                    "Shared queue pump cycle failed. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, FailureReason={FailureReason}",
                    controlPlaneId,
                    request.PumpRuntimeInstanceId,
                    workerId,
                    request.TenantId,
                    request.PipelineKey,
                    dispatchResults.Count,
                    successfulDispatches,
                    failedDispatches,
                    stoppedBecauseNoItemAvailable,
                    durationMs,
                    exception.Message);

                return new AiSharedQueuePumpResult
                {
                    Success = false,
                    RuntimeInstanceId = request.PumpRuntimeInstanceId,
                    AttemptedDispatchCount = dispatchResults.Count,
                    SuccessfulDispatchCount = successfulDispatches,
                    FailedDispatchCount = failedDispatches,
                    StoppedBecauseNoItemAvailable = stoppedBecauseNoItemAvailable,
                    FailureReason = exception.Message,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    DispatchResults = dispatchResults.ToArray(),
                    Diagnostics = new[] { exception.Message }
                };
            }
        }

        /// <summary>
        /// Records a shared queue pump control-plane event.
        /// </summary>
        /// <param name="eventType">The control-plane event type.</param>
        /// <param name="request">The pump request.</param>
        /// <param name="workerId">The resolved worker identifier.</param>
        /// <param name="outcome">The optional control-plane operation outcome.</param>
        /// <param name="failureReason">The optional failure reason.</param>
        /// <param name="properties">The optional event properties.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>A task that completes when the control-plane event has been recorded.</returns>
        private async Task RecordSharedQueuePumpEventAsync(
            AiControlPlaneEventType eventType,
            AiSharedQueuePumpRequest request,
            string? workerId,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await _observer.RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.SharedQueue,
                            Operation = SharedQueuePumpOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                                    ? Guid.NewGuid().ToString("N")
                                    : request.CorrelationId,
                                RuntimeInstanceId = request.PumpRuntimeInstanceId,
                                WorkerId = workerId,
                                PipelineKey = request.PipelineKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    [AiRuntimeInstanceIsolationMetadataKeys.CamelCaseTenantId] = request.TenantId,
                                    [AiPipelineMetadataKeys.CamelCasePipelineKey] = request.PipelineKey,
                                    [AiRuntimeInstanceMetadataKeys.CamelCaseRuntimeInstanceId] = request.PumpRuntimeInstanceId,
                                    [AiWorkerMetadataKeys.CamelCaseWorkerId] = workerId
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break shared queue pumping.
            }
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
        /// Resolves the maximum number of dispatch attempts for the pump cycle.
        /// </summary>
        /// <param name="request">The pump request.</param>
        /// <returns>The maximum number of dispatch attempts.</returns>
        private int ResolveMaxDispatches(
            AiSharedQueuePumpRequest request)
        {
            var value = request.MaxDispatches ?? _options.MaxDispatchesPerCycle;

            return Math.Max(1, value);
        }

        /// <summary>
        /// Resolves the claim TTL for queue item claims.
        /// </summary>
        /// <param name="request">The pump request.</param>
        /// <returns>The claim TTL.</returns>
        private TimeSpan ResolveClaimTtl(
            AiSharedQueuePumpRequest request)
        {
            var value = request.ClaimTtl ?? _options.DefaultClaimTtl;

            return value <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(30)
                : value;
        }

        /// <summary>
        /// Resolves the worker id used for the pump cycle.
        /// </summary>
        /// <param name="request">The pump request.</param>
        /// <returns>The worker id.</returns>
        private string? ResolveWorkerId(
            AiSharedQueuePumpRequest request)
        {
            return string.IsNullOrWhiteSpace(request.PumpWorkerId)
                ? _options.WorkerId
                : request.PumpWorkerId;
        }

        /// <summary>
        /// Resolves the source label used for the pump cycle.
        /// </summary>
        /// <param name="request">The pump request.</param>
        /// <returns>The source label.</returns>
        private string ResolveSource(
            AiSharedQueuePumpRequest request)
        {
            return string.IsNullOrWhiteSpace(request.Source)
                ? _options.Source
                : request.Source;
        }

        /// <summary>
        /// Resolves the logical control-plane identifier from pump metadata.
        /// </summary>
        /// <param name="metadata">The pump metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Metadata = metadata,
                            Source = "shared-queue-pump",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return controlPlaneId;
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
        /// <param name="sources">The metadata sources.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            params IReadOnlyDictionary<string, string>[] sources)
        {
            var merged =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var source in sources)
            {
                foreach (var pair in source)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
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
        /// Builds compact diagnostics from dispatch results.
        /// </summary>
        /// <param name="dispatchResults">The dispatch results.</param>
        /// <returns>The compact diagnostics.</returns>
        private static IReadOnlyList<string> BuildDiagnostics(
            IReadOnlyList<AiSharedQueueDispatchResult> dispatchResults)
        {
            var diagnostics = dispatchResults
                .Where(result => !string.IsNullOrWhiteSpace(result.FailureReason))
                .Select(result => result.FailureReason!)
                .ToArray();

            return diagnostics.Length == 0
                ? Array.Empty<string>()
                : diagnostics;
        }
    }
}