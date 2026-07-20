using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
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
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions;
        private readonly IOptions<AiMcpControlPlaneHostOptions> hostOptions;
        private readonly ILogger<AiMcpControlPlaneBackgroundService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiMcpControlPlaneBackgroundService"/> class.
        /// </summary>
        /// <param name="sharedQueuePump">The shared queue pump.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="capacityStores">The runtime instance capacity stores.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane id resolver.</param>
        /// <param name="queueOptions">The shared queue background service options.</param>
        /// <param name="hostOptions">The MCP control-plane host options.</param>
        /// <param name="logger">The logger.</param>
        public AiMcpControlPlaneBackgroundService(
            IAiSharedQueuePump sharedQueuePump,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiSharedQueueBackgroundServiceOptions> queueOptions,
            IOptions<AiMcpControlPlaneHostOptions> hostOptions,
            ILogger<AiMcpControlPlaneBackgroundService> logger)
        {
            this.sharedQueuePump = sharedQueuePump ?? throw new ArgumentNullException(nameof(sharedQueuePump));
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.capacityStores = capacityStores?.ToArray()
                ?? throw new ArgumentNullException(nameof(capacityStores));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.queueOptions = queueOptions ?? throw new ArgumentNullException(nameof(queueOptions));
            this.hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            var host = this.hostOptions.Value;
            var queue = this.queueOptions.Value;

            if (!host.Enabled || !host.EnableSharedQueuePump || !queue.Enabled)
            {
                this.logger.LogInformation(
                    "MCP control-plane background service disabled. HostEnabled={HostEnabled}, SharedQueuePumpEnabled={SharedQueuePumpEnabled}, QueueEnabled={QueueEnabled}",
                    host.Enabled,
                    host.EnableSharedQueuePump,
                    queue.Enabled);

                return;
            }

            if (string.IsNullOrWhiteSpace(host.RuntimeInstanceId))
            {
                this.logger.LogWarning(
                    "MCP control-plane background service disabled because RuntimeInstanceId is empty.");

                return;
            }

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(stoppingToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "MCP control-plane background service started. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, MetadataControlPlaneId={MetadataControlPlaneId}, WaitForRuntimeReadiness={WaitForRuntimeReadiness}",
                host.RuntimeInstanceId,
                host.WorkerId,
                controlPlaneId,
                queue.WaitForRuntimeReadiness);

            if (queue.WaitForRuntimeReadiness)
            {
                await this.WaitForRuntimeReadinessAsync(
                        queue,
                        stoppingToken)
                    .ConfigureAwait(false);
            }

            this.logger.LogInformation(
                "MCP control-plane shared queue pump loop starting. RuntimeInstanceId={RuntimeInstanceId}, WorkerId={WorkerId}, MetadataControlPlaneId={MetadataControlPlaneId}",
                host.RuntimeInstanceId,
                host.WorkerId,
                controlPlaneId);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var metadata =
                        CreatePumpMetadata(
                            controlPlaneId,
                            host.RuntimeInstanceId,
                            host.WorkerId);

                    Console.WriteLine(
                        $"[MCP SHARED QUEUE PUMP REQUEST] MetadataControlPlaneId='{controlPlaneId}', RuntimeInstanceId='{host.RuntimeInstanceId}', WorkerId='{host.WorkerId}', MaxDispatches='{queue.MaxDispatchesPerCycle}', ClaimTtl='{queue.ClaimTtl}', Source='mcp-control-plane'.");

                    var result = await this.sharedQueuePump
                        .PumpOnceAsync(
                            new AiSharedQueuePumpRequest
                            {
                                PumpRuntimeInstanceId = host.RuntimeInstanceId,
                                PumpWorkerId = host.WorkerId,
                                MaxDispatches = queue.MaxDispatchesPerCycle,
                                ClaimTtl = queue.ClaimTtl,
                                Source = "mcp-control-plane",
                                RequestedBy = "mcp-server",
                                Metadata = metadata
                            },
                            stoppingToken)
                        .ConfigureAwait(false);

                    this.logger.LogInformation(
                        "MCP shared queue pump cycle completed. Success={Success}, MetadataControlPlaneId={MetadataControlPlaneId}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, FailureReason={FailureReason}",
                        result.Success,
                        controlPlaneId,
                        result.AttemptedDispatchCount,
                        result.SuccessfulDispatchCount,
                        result.FailedDispatchCount,
                        result.FailureReason);

                    var delay = result.SuccessfulDispatchCount > 0
                        ? queue.ActiveDelay
                        : queue.IdleDelay;

                    this.logger.LogDebug(
                        "MCP shared queue pump cycle details. Success={Success}, MetadataControlPlaneId={MetadataControlPlaneId}, AttemptedDispatchCount={AttemptedDispatchCount}, SuccessfulDispatchCount={SuccessfulDispatchCount}, FailedDispatchCount={FailedDispatchCount}, StoppedBecauseNoItemAvailable={StoppedBecauseNoItemAvailable}, DurationMs={DurationMs}, DelayMs={DelayMs}, FailureReason={FailureReason}",
                        result.Success,
                        controlPlaneId,
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
                    this.logger.LogError(
                        exception,
                        "MCP shared queue pump cycle failed.");

                    await Task.Delay(
                            queue.ErrorDelay,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
            }

            this.logger.LogInformation(
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

            this.logger.LogInformation(
                "MCP shared queue pump readiness gate started. PollIntervalMs={PollIntervalMs}, TimeoutMs={TimeoutMs}, CapacityStoreCount={CapacityStoreCount}",
                pollInterval.TotalMilliseconds,
                queue.RuntimeReadinessTimeout?.TotalMilliseconds,
                this.capacityStores.Count);

            while (!cancellationToken.IsCancellationRequested)
            {
                attempt++;

                if (queue.RuntimeReadinessTimeout is not null &&
                    DateTimeOffset.UtcNow - startedAtUtc >= queue.RuntimeReadinessTimeout.Value)
                {
                    this.logger.LogWarning(
                        "MCP shared queue pump readiness gate timed out. Attempt={Attempt}, TimeoutMs={TimeoutMs}. Pump will continue and rely on admission to reject unavailable runtimes.",
                        attempt,
                        queue.RuntimeReadinessTimeout.Value.TotalMilliseconds);

                    return;
                }

                var runtimeInstances =
                    await this.runtimeInstanceRegistry
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
                    this.logger.LogInformation(
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

                if (this.capacityStores.Count == 0)
                {
                    this.logger.LogInformation(
                        "MCP shared queue pump readiness completed without capacity stores. Attempt={Attempt}, ReadyRuntimeInstanceCount={ReadyRuntimeInstanceCount}",
                        attempt,
                        readyRuntimeInstances.Length);

                    return;
                }

                foreach (var capacityStore in this.capacityStores)
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
                        this.logger.LogInformation(
                            "MCP shared queue pump readiness completed. Attempt={Attempt}, RuntimeInstanceId={RuntimeInstanceId}, AvailableRunSlots={AvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, AvailableWorkerCount={AvailableWorkerCount}",
                            attempt,
                            readyDescriptor.RuntimeInstanceId,
                            readyDescriptor.AvailableRunSlots,
                            readyDescriptor.EffectiveAvailableRunSlots,
                            readyDescriptor.AvailableWorkerCount);

                        return;
                    }
                }

                this.logger.LogInformation(
                    "MCP shared queue pump readiness waiting. Attempt={Attempt}, Reason={Reason}, ReadyRuntimeInstanceCount={ReadyRuntimeInstanceCount}, CapacityStoreCount={CapacityStoreCount}",
                    attempt,
                    "Ready runtime instances exist but no matching ready capacity descriptor is visible yet.",
                    readyRuntimeInstances.Length,
                    this.capacityStores.Count);

                await Task.Delay(
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used to scope shared queue pump reads.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            return await this.controlPlaneIdResolver
                .ResolveAsync(
                    new AiControlPlaneIdResolutionRequest
                    {
                        Source = "mcp-control-plane-background-service",
                        AllowGeneratedFallback = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates metadata forwarded to the shared queue pump and dispatcher.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The pump runtime instance identifier.</param>
        /// <param name="workerId">The pump worker identifier.</param>
        /// <returns>The pump metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreatePumpMetadata(
            string controlPlaneId,
            string runtimeInstanceId,
            string? workerId)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controlPlaneId"] = controlPlaneId,
                ["logicalControlPlaneId"] = controlPlaneId,
                ["runtime.controlPlaneId"] = controlPlaneId,
                ["mcp.controlPlaneId"] = controlPlaneId,
                ["pump.runtimeInstanceId"] = runtimeInstanceId,
                ["pump.workerId"] = workerId ?? string.Empty
            };
        }
    }
}