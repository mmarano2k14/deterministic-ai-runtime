using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;

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
    /// </remarks>
    public sealed class AiSharedQueuePump : IAiSharedQueuePump
    {
        private readonly IAiSharedQueueDispatcher _dispatcher;
        private readonly AiSharedQueuePumpOptions _options;
        private readonly ILogger<AiSharedQueuePump> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueuePump"/> class.
        /// </summary>
        /// <param name="dispatcher">The shared queue dispatcher.</param>
        /// <param name="options">The pump options.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="dispatcher"/>, <paramref name="options"/>, or <paramref name="logger"/> is null.
        /// </exception>
        public AiSharedQueuePump(
            IAiSharedQueueDispatcher dispatcher,
            IOptions<AiSharedQueuePumpOptions> options,
            ILogger<AiSharedQueuePump> logger)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiSharedQueuePumpResult> PumpOnceAsync(
            AiSharedQueuePumpRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PumpRuntimeInstanceId);

            var startedAtUtc = DateTimeOffset.UtcNow;

            if (!_options.Enabled)
            {
                var disabledCompletedAtUtc = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "Shared queue pump skipped because it is disabled. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}",
                    request.PumpRuntimeInstanceId,
                    request.PumpWorkerId,
                    request.TenantId,
                    request.PipelineKey);

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

            _logger.LogInformation(
                "Shared queue pump cycle started. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, MaxDispatches={MaxDispatches}, ClaimTtlMs={ClaimTtlMs}, Source={Source}, Reason={Reason}",
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
                        "Shared queue pump dispatch attempt started. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, MaxDispatches={MaxDispatches}, TenantId={TenantId}, PipelineKey={PipelineKey}",
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
                                Metadata = request.Metadata
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                    dispatchResults.Add(dispatchResult);

                    if (dispatchResult.NoItemAvailable)
                    {
                        stoppedBecauseNoItemAvailable = true;

                        _logger.LogDebug(
                            "Shared queue pump dispatch attempt found no item. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, TenantId={TenantId}, PipelineKey={PipelineKey}, StopCycleWhenNoItemAvailable={StopCycleWhenNoItemAvailable}",
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
                            "Shared queue pump dispatch attempt succeeded. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, TenantId={TenantId}, PipelineKey={PipelineKey}, Diagnostics={Diagnostics}",
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
                        "Shared queue pump dispatch attempt failed. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, AttemptIndex={AttemptIndex}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, TenantId={TenantId}, PipelineKey={PipelineKey}, FailureReason={FailureReason}, Diagnostics={Diagnostics}, StopCycleOnDispatchFailure={StopCycleOnDispatchFailure}",
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

                _logger.LogInformation(
                    "Shared queue pump cycle completed. Success=True, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, Diagnostics={Diagnostics}",
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

                _logger.LogError(
                    exception,
                    "Shared queue pump cycle failed. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, TenantId={TenantId}, PipelineKey={PipelineKey}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, FailureReason={FailureReason}",
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
        /// Resolves the maximum number of dispatch attempts for the pump cycle.
        /// </summary>
        private int ResolveMaxDispatches(
            AiSharedQueuePumpRequest request)
        {
            var value = request.MaxDispatches ?? _options.MaxDispatchesPerCycle;

            return Math.Max(1, value);
        }

        /// <summary>
        /// Resolves the claim TTL for queue item claims.
        /// </summary>
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
        private string ResolveSource(
            AiSharedQueuePumpRequest request)
        {
            return string.IsNullOrWhiteSpace(request.Source)
                ? _options.Source
                : request.Source;
        }

        /// <summary>
        /// Calculates duration in milliseconds.
        /// </summary>
        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Builds compact diagnostics from dispatch results.
        /// </summary>
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