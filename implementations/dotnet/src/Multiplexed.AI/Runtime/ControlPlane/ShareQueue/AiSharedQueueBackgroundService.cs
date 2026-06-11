using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
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
    /// - resolve and propagate the logical control-plane identifier
    /// - delay between cycles
    /// - apply simple error backoff
    ///
    /// It does not decide admission.
    /// It does not scale Kubernetes.
    /// It does not execute DAG steps directly.
    /// It does not build Redis keys directly.
    /// </remarks>
    public sealed class AiSharedQueueBackgroundService : BackgroundService
    {
        private readonly IAiSharedQueuePump _pump;
        private readonly AiSharedQueueBackgroundServiceOptions _options;
        private readonly IAiControlPlaneIdResolver _controlPlaneIdResolver;
        private readonly ILogger<AiSharedQueueBackgroundService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueBackgroundService"/> class.
        /// </summary>
        /// <param name="pump">The shared queue pump.</param>
        /// <param name="options">The background service options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="pump"/>, <paramref name="options"/>,
        /// <paramref name="controlPlaneIdResolver"/>, or <paramref name="logger"/> is null.
        /// </exception>
        public AiSharedQueueBackgroundService(
            IAiSharedQueuePump pump,
            IOptions<AiSharedQueueBackgroundServiceOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            ILogger<AiSharedQueueBackgroundService> logger)
        {
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
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

            var controlPlaneId =
                await ResolveControlPlaneIdAsync(stoppingToken)
                    .ConfigureAwait(false);

            var metadata =
                MergeMetadata(
                    _options.Metadata,
                    new Dictionary<string, string>
                    {
                        ["controlPlaneId"] = controlPlaneId
                    });

            _logger.LogInformation(
                "AI shared queue background service started for ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}.",
                controlPlaneId,
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
                                Metadata = metadata
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    LogPumpResult(
                        result,
                        controlPlaneId);

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
                        "AI shared queue background service cycle failed. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}.",
                        controlPlaneId,
                        runtimeInstanceId,
                        workerId);

                    await DelayAsync(
                            _options.ErrorDelay,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
            }

            _logger.LogInformation(
                "AI shared queue background service stopped for ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}.",
                controlPlaneId,
                runtimeInstanceId,
                workerId);
        }

        /// <summary>
        /// Logs the result of one pump cycle.
        /// </summary>
        /// <param name="result">The pump result.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        private void LogPumpResult(
            AiSharedQueuePumpResult result,
            string controlPlaneId)
        {
            if (!result.Success)
            {
                _logger.LogWarning(
                    "AI shared queue pump cycle failed for ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}. FailureReason={FailureReason}",
                    controlPlaneId,
                    result.RuntimeInstanceId,
                    result.FailureReason);

                return;
            }

            if (result.SuccessfulDispatchCount > 0 ||
                result.FailedDispatchCount > 0)
            {
                _logger.LogInformation(
                    "AI shared queue pump cycle completed for ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}. Attempted={Attempted}, Success={Success}, Failed={Failed}, NoItem={NoItem}.",
                    controlPlaneId,
                    result.RuntimeInstanceId,
                    result.AttemptedDispatchCount,
                    result.SuccessfulDispatchCount,
                    result.FailedDispatchCount,
                    result.StoppedBecauseNoItemAvailable);
            }
            else
            {
                _logger.LogDebug(
                    "AI shared queue pump cycle completed with no item for ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}.",
                    controlPlaneId,
                    result.RuntimeInstanceId);
            }
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used by the shared queue background service.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var controlPlaneId =
                await _controlPlaneIdResolver
                    .ResolveAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            return controlPlaneId;
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
        /// Merges metadata dictionaries into an immutable dictionary shape.
        /// </summary>
        /// <param name="sources">The metadata sources to merge.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            params IReadOnlyDictionary<string, string>[] sources)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            foreach (var source in sources)
            {
                foreach (var item in source)
                {
                    if (!string.IsNullOrWhiteSpace(item.Key))
                    {
                        result[item.Key] = item.Value;
                    }
                }
            }

            return result;
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