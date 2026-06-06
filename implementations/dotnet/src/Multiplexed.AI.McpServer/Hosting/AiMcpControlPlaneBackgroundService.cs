using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;

namespace Multiplexed.AI.McpServer.Hosting
{
    /// <summary>
    /// Runs MCP control-plane background work by consuming the shared queue pump.
    /// </summary>
    /// <remarks>
    /// This service does not execute DAG steps directly.
    /// It only consumes the shared queue control-plane pump so queued shared runs can be
    /// dispatched automatically to registered runtime instances.
    /// </remarks>
    public sealed class AiMcpControlPlaneBackgroundService : BackgroundService
    {
        private readonly IAiSharedQueuePump sharedQueuePump;
        private readonly IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions;
        private readonly IOptions<AiMcpControlPlaneHostOptions> hostOptions;
        private readonly ILogger<AiMcpControlPlaneBackgroundService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiMcpControlPlaneBackgroundService"/> class.
        /// </summary>
        /// <param name="sharedQueuePump">The shared queue pump.</param>
        /// <param name="queueOptions">The shared queue background service options.</param>
        /// <param name="hostOptions">The MCP control-plane host options.</param>
        /// <param name="logger">The logger.</param>
        public AiMcpControlPlaneBackgroundService(
            IAiSharedQueuePump sharedQueuePump,
            IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions,
            IOptions<AiMcpControlPlaneHostOptions> hostOptions,
            ILogger<AiMcpControlPlaneBackgroundService> logger)
        {
            this.sharedQueuePump = sharedQueuePump ?? throw new ArgumentNullException(nameof(sharedQueuePump));
            this.queueOptions = queueOptions ?? throw new ArgumentNullException(nameof(queueOptions));
            this.hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var host = hostOptions.Value;
            var queue = queueOptions.Value;

            if (!host.Enabled || !host.EnableSharedQueuePump || !queue.Enabled)
            {
                logger.LogInformation(
                    "MCP control-plane background service disabled. HostEnabled={HostEnabled}, SharedQueuePumpEnabled={SharedQueuePumpEnabled}, QueueEnabled={QueueEnabled}",
                    host.Enabled,
                    host.EnableSharedQueuePump,
                    queue.Enabled);

                return;
            }

            if (string.IsNullOrWhiteSpace(host.RuntimeInstanceId))
            {
                logger.LogWarning(
                    "MCP control-plane background service disabled because RuntimeInstanceId is empty.");

                return;
            }

            logger.LogInformation(
                "MCP control-plane background service started. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}",
                host.RuntimeInstanceId,
                host.WorkerId);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await sharedQueuePump
                        .PumpOnceAsync(
                            new AiSharedQueuePumpRequest
                            {
                                RuntimeInstanceId = host.RuntimeInstanceId,
                                WorkerId = host.WorkerId,
                                MaxDispatches = queue.MaxDispatchesPerCycle,
                                ClaimTtl = queue.ClaimTtl,
                                Source = "mcp-control-plane",
                                RequestedBy = "mcp-server"
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    var delay = result.SuccessfulDispatchCount > 0
                        ? queue.ActiveDelay
                        : queue.IdleDelay;

                    logger.LogDebug(
                        "MCP shared queue pump cycle completed. Success={Success}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, DelayMs={DelayMs}, FailureReason={FailureReason}",
                        result.Success,
                        result.AttemptedDispatchCount,
                        result.SuccessfulDispatchCount,
                        result.FailedDispatchCount,
                        result.StoppedBecauseNoItemAvailable,
                        result.DurationMs,
                        delay.TotalMilliseconds,
                        result.FailureReason);

                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "MCP shared queue pump cycle failed.");

                    await Task.Delay(queue.ErrorDelay, stoppingToken).ConfigureAwait(false);
                }
            }

            logger.LogInformation("MCP control-plane background service stopped.");
        }
    }
}