using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedQueue
{
    /// <summary>
    /// Background service that continuously pumps the shared queue.
    /// </summary>
    /// <remarks>
    /// This hosted service is intentionally thin.
    /// The dispatch logic lives in <see cref="IAiSharedQueuePump"/>.
    ///
    /// Responsibilities:
    /// - run periodic pump cycles
    /// - provide runtime instance identity / worker identity
    /// - delay between cycles
    /// - apply simple error backoff
    ///
    /// It does not decide admission.
    /// It does not scale Kubernetes.
    /// It does not execute DAG steps directly.
    /// </remarks>
    public sealed class AiSharedQueueBackgroundService : BackgroundService
    {
        private readonly IAiSharedQueuePump _pump;
        private readonly AiSharedQueueBackgroundServiceOptions _options;
        private readonly ILogger<AiSharedQueueBackgroundService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueBackgroundService"/> class.
        /// </summary>
        /// <param name="pump">The shared queue pump.</param>
        /// <param name="options">The background service options.</param>
        /// <param name="logger">The logger.</param>
        public AiSharedQueueBackgroundService(
            IAiSharedQueuePump pump,
            IOptions<AiSharedQueueBackgroundServiceOptions> options,
            ILogger<AiSharedQueueBackgroundService> logger)
        {
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation(
                    "AI shared queue background service is disabled.");

                return;
            }

            var runtimeInstanceId = ResolveRuntimeInstanceId();
            var workerId = ResolveWorkerId(runtimeInstanceId);

            _logger.LogInformation(
                "AI shared queue background service started for RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}.",
                runtimeInstanceId,
                workerId);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await _pump
                        .PumpOnceAsync(
                            new AiSharedQueuePumpRequest
                            {
                                PumpRuntimeInstanceId = runtimeInstanceId,
                                PumpWorkerId = workerId,
                                TenantId = _options.TenantId,
                                PipelineKey = _options.PipelineKey,
                                MaxDispatches = _options.MaxDispatchesPerCycle,
                                ClaimTtl = _options.ClaimTtl,
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                RequestedBy = _options.RequestedBy,
                                Source = _options.Source,
                                Reason = "Shared queue background service pump cycle.",
                                Metadata = _options.Metadata
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    LogPumpResult(result);

                    var delay = result.SuccessfulDispatchCount > 0
                        ? _options.ActiveDelay
                        : _options.IdleDelay;

                    await DelayAsync(
                            delay,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "AI shared queue background service cycle failed.");

                    await DelayAsync(
                            _options.ErrorDelay,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
            }

            _logger.LogInformation(
                "AI shared queue background service stopped for RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}.",
                runtimeInstanceId,
                workerId);
        }

        /// <summary>
        /// Logs the result of one pump cycle.
        /// </summary>
        /// <param name="result">The pump result.</param>
        private void LogPumpResult(
            AiSharedQueuePumpResult result)
        {
            if (!result.Success)
            {
                _logger.LogWarning(
                    "AI shared queue pump cycle failed for RuntimeInstanceId={RuntimeInstanceId}. FailureReason={FailureReason}",
                    result.RuntimeInstanceId,
                    result.FailureReason);

                return;
            }

            if (result.SuccessfulDispatchCount > 0 ||
                result.FailedDispatchCount > 0)
            {
                _logger.LogInformation(
                    "AI shared queue pump cycle completed for RuntimeInstanceId={RuntimeInstanceId}. Attempted={Attempted}, Success={Success}, Failed={Failed}, NoItem={NoItem}.",
                    result.RuntimeInstanceId,
                    result.AttemptedDispatchCount,
                    result.SuccessfulDispatchCount,
                    result.FailedDispatchCount,
                    result.StoppedBecauseNoItemAvailable);
            }
            else
            {
                _logger.LogDebug(
                    "AI shared queue pump cycle completed with no item for RuntimeInstanceId={RuntimeInstanceId}.",
                    result.RuntimeInstanceId);
            }
        }

        /// <summary>
        /// Resolves runtime instance id for the background service.
        /// </summary>
        /// <returns>The runtime instance id.</returns>
        private string ResolveRuntimeInstanceId()
        {
            if (!string.IsNullOrWhiteSpace(_options.RuntimeInstanceId))
            {
                return _options.RuntimeInstanceId;
            }

            return $"{Environment.MachineName}-{Environment.ProcessId}";
        }

        /// <summary>
        /// Resolves worker id for the background service.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The worker id.</returns>
        private string ResolveWorkerId(
            string runtimeInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(_options.WorkerId))
            {
                return _options.WorkerId;
            }

            return $"{runtimeInstanceId}-shared-queue-worker";
        }

        /// <summary>
        /// Delays safely.
        /// </summary>
        /// <param name="delay">The delay.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private static Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var safeDelay = delay <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : delay;

            return Task.Delay(
                safeDelay,
                cancellationToken);
        }
    }
}