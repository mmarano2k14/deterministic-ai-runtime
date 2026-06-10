using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
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
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IReadOnlyCollection<IAiRuntimeInstanceCapacityStore> capacityStores;
        private readonly IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions;
        private readonly IOptions<AiMcpControlPlaneHostOptions> hostOptions;
        private readonly ILogger<AiMcpControlPlaneBackgroundService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiMcpControlPlaneBackgroundService"/> class.
        /// </summary>
        /// <param name="sharedQueuePump">The shared queue pump.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="capacityStores">The runtime instance capacity stores.</param>
        /// <param name="queueOptions">The shared queue background service options.</param>
        /// <param name="hostOptions">The MCP control-plane host options.</param>
        /// <param name="logger">The logger.</param>
        public AiMcpControlPlaneBackgroundService(
            IAiSharedQueuePump sharedQueuePump,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions,
            IOptions<AiMcpControlPlaneHostOptions> hostOptions,
            ILogger<AiMcpControlPlaneBackgroundService> logger)
        {
            this.sharedQueuePump = sharedQueuePump ?? throw new ArgumentNullException(nameof(sharedQueuePump));
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.capacityStores = capacityStores?.ToArray()
                ?? throw new ArgumentNullException(nameof(capacityStores));
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
                "MCP control-plane background service started. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, WaitForRuntimeReadiness={WaitForRuntimeReadiness}",
                host.RuntimeInstanceId,
                host.WorkerId,
                queue.WaitForRuntimeReadiness);

            if (queue.WaitForRuntimeReadiness)
            {
                await WaitForRuntimeReadinessAsync(
                        queue,
                        stoppingToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation(
                "MCP control-plane shared queue pump loop starting. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}",
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
                                PumpRuntimeInstanceId = host.RuntimeInstanceId,
                                PumpWorkerId = host.WorkerId,
                                MaxDispatches = queue.MaxDispatchesPerCycle,
                                ClaimTtl = queue.ClaimTtl,
                                Source = "mcp-control-plane",
                                RequestedBy = "mcp-server"
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    logger.LogInformation(
                        "MCP shared queue pump cycle completed. Success={Success}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, FailureReason={FailureReason}",
                        result.Success,
                        result.AttemptedDispatchCount,
                        result.SuccessfulDispatchCount,
                        result.FailedDispatchCount,
                        result.FailureReason);

                    var delay = result.SuccessfulDispatchCount > 0
                        ? queue.ActiveDelay
                        : queue.IdleDelay;

                    logger.LogDebug(
                        "MCP shared queue pump cycle details. Success={Success}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, DelayMs={DelayMs}, FailureReason={FailureReason}",
                        result.Success,
                        result.AttemptedDispatchCount,
                        result.SuccessfulDispatchCount,
                        result.FailedDispatchCount,
                        result.StoppedBecauseNoItemAvailable,
                        result.DurationMs,
                        delay.TotalMilliseconds,
                        result.FailureReason);

                    await Task.Delay(
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
                    logger.LogError(
                        exception,
                        "MCP shared queue pump cycle failed.");

                    await Task.Delay(
                            queue.ErrorDelay,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
            }

            logger.LogInformation(
                "MCP control-plane background service stopped.");
        }

        /// <summary>
        /// Waits until at least one runtime instance is visible and has a matching capacity descriptor.
        /// </summary>
        /// <param name="queue">The shared queue background service options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task WaitForRuntimeReadinessAsync(
            AiSharedQueueBackgroundServiceOptions queue,
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            var startedAtUtc = DateTimeOffset.UtcNow;

            var pollInterval = queue.RuntimeReadinessPollInterval <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(250)
                : queue.RuntimeReadinessPollInterval;

            logger.LogInformation(
                "MCP shared queue pump readiness gate started. PollIntervalMs={PollIntervalMs}, TimeoutMs={TimeoutMs}, CapacityStoreCount={CapacityStoreCount}",
                pollInterval.TotalMilliseconds,
                queue.RuntimeReadinessTimeout?.TotalMilliseconds,
                capacityStores.Count);

            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;

                if (queue.RuntimeReadinessTimeout is not null &&
                    DateTimeOffset.UtcNow - startedAtUtc >= queue.RuntimeReadinessTimeout.Value)
                {
                    logger.LogWarning(
                        "MCP shared queue pump readiness gate timed out. Attempt={Attempt}, TimeoutMs={TimeoutMs}. Pump will continue and rely on admission to reject unavailable runtimes.",
                        attempt,
                        queue.RuntimeReadinessTimeout.Value.TotalMilliseconds);

                    return;
                }

                var runtimeInstances =
                    await runtimeInstanceRegistry
                        .ListAsync(
                            includeStopped: false,
                            cancellationToken)
                        .ConfigureAwait(false);

                var readyRuntimeInstances =
                    runtimeInstances
                        .Where(instance =>
                            instance.Role == AiRuntimeInstanceRole.Runtime &&
                            instance.Status == AiRuntimeInstanceStatus.Ready &&
                            instance.CanAcceptRun)
                        .ToArray();

                if (readyRuntimeInstances.Length == 0)
                {
                    logger.LogInformation(
                        "MCP shared queue pump readiness waiting. Attempt={Attempt}, Reason={Reason}, RegisteredInstanceCount={RegisteredInstanceCount}",
                        attempt,
                        "No ready runtime instance can accept runs.",
                        runtimeInstances.Count);

                    await Task.Delay(
                            pollInterval,
                            cancellationToken)
                        .ConfigureAwait(false);

                    continue;
                }

                if (capacityStores.Count == 0)
                {
                    logger.LogInformation(
                        "MCP shared queue pump readiness completed without capacity stores. Attempt={Attempt}, ReadyRuntimeInstanceCount={ReadyRuntimeInstanceCount}",
                        attempt,
                        readyRuntimeInstances.Length);

                    return;
                }

                foreach (var capacityStore in capacityStores)
                {
                    var descriptors =
                        await capacityStore
                            .ListAsync(cancellationToken)
                            .ConfigureAwait(false);

                    var readyDescriptor =
                        descriptors.FirstOrDefault(descriptor =>
                            descriptor.Role == AiRuntimeInstanceRole.Runtime &&
                            descriptor.Status == AiRuntimeInstanceStatus.Ready &&
                            descriptor.CanAcceptRun &&
                            readyRuntimeInstances.Any(instance =>
                                string.Equals(
                                    instance.RuntimeInstanceId,
                                    descriptor.RuntimeInstanceId,
                                    StringComparison.Ordinal)));

                    if (readyDescriptor is not null)
                    {
                        logger.LogInformation(
                            "MCP shared queue pump readiness completed. Attempt={Attempt}, RuntimeInstanceId={RuntimeInstanceId}, AvailableRunSlots={AvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, AvailableWorkerCount={AvailableWorkerCount}",
                            attempt,
                            readyDescriptor.RuntimeInstanceId,
                            readyDescriptor.AvailableRunSlots,
                            readyDescriptor.EffectiveAvailableRunSlots,
                            readyDescriptor.AvailableWorkerCount);

                        return;
                    }
                }

                logger.LogInformation(
                    "MCP shared queue pump readiness waiting. Attempt={Attempt}, Reason={Reason}, ReadyRuntimeInstanceCount={ReadyRuntimeInstanceCount}, CapacityStoreCount={CapacityStoreCount}",
                    attempt,
                    "Ready runtime instances exist but no matching ready capacity descriptor is visible yet.",
                    readyRuntimeInstances.Length,
                    capacityStores.Count);

                await Task.Delay(
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}