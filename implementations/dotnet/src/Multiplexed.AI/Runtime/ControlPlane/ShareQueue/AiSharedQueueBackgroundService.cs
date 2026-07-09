using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;

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
    /// - optionally wait for runtime registry and capacity readiness before pumping
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
        private readonly IAiRuntimeInstanceRegistry _runtimeInstanceRegistry;
        private readonly IReadOnlyCollection<IAiRuntimeInstanceCapacityStore> _runtimeInstanceCapacityStores;
        private readonly ILogger<AiSharedQueueBackgroundService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSharedQueueBackgroundService"/> class.
        /// </summary>
        /// <param name="pump">The shared queue pump.</param>
        /// <param name="options">The background service options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane identifier resolver.</param>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStores">The runtime instance capacity stores.</param>
        /// <param name="logger">The logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="pump"/>, <paramref name="options"/>,
        /// <paramref name="controlPlaneIdResolver"/>, <paramref name="runtimeInstanceRegistry"/>,
        /// <paramref name="runtimeInstanceCapacityStores"/>, or <paramref name="logger"/> is null.
        /// </exception>
        public AiSharedQueueBackgroundService(
            IAiSharedQueuePump pump,
            IOptions<AiSharedQueueBackgroundServiceOptions> options,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IEnumerable<IAiRuntimeInstanceCapacityStore> runtimeInstanceCapacityStores,
            ILogger<AiSharedQueueBackgroundService> logger)
        {
            _pump = pump ?? throw new ArgumentNullException(nameof(pump));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            _runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            _runtimeInstanceCapacityStores = runtimeInstanceCapacityStores?.ToArray()
                ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStores));
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

            var controlPlaneMetadata =
                await _controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = controlPlaneId,
                            Metadata = _options.Metadata,
                            Source = "shared-queue-background-service-metadata",
                            AllowGeneratedFallback = false
                        },
                        stoppingToken)
                    .ConfigureAwait(false);

            var metadata =
                MergeMetadata(
                    _options.Metadata,
                    controlPlaneMetadata);

            await WaitForRuntimeReadinessAsync(
                    controlPlaneId,
                    runtimeInstanceId,
                    stoppingToken)
                .ConfigureAwait(false);

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
        /// Waits until the configured runtime instance is visible in the registry and has
        /// a matching capacity descriptor before the shared queue pump loop starts.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task WaitForRuntimeReadinessAsync(
            string controlPlaneId,
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (!_options.WaitForRuntimeReadiness)
            {
                _logger.LogInformation(
                    "Runtime readiness wait skipped before shared queue pump start. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}.",
                    controlPlaneId,
                    runtimeInstanceId);

                return;
            }

            var startedAtUtc = DateTimeOffset.UtcNow;
            var deadlineUtc = _options.RuntimeReadinessTimeout.HasValue
                ? startedAtUtc.Add(_options.RuntimeReadinessTimeout.Value)
                : (DateTimeOffset?)null;

            var pollInterval = NormalizeDelay(
                _options.RuntimeReadinessPollInterval);

            string? lastRegistryReason = null;
            string? lastCapacityReason = null;

            _logger.LogInformation(
                "Runtime readiness wait started before shared queue pump start. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, Timeout={Timeout}, PollIntervalMs={PollIntervalMs}, CapacityStoreCount={CapacityStoreCount}.",
                controlPlaneId,
                runtimeInstanceId,
                _options.RuntimeReadinessTimeout?.ToString() ?? "infinite",
                pollInterval.TotalMilliseconds,
                _runtimeInstanceCapacityStores.Count);

            while (!cancellationToken.IsCancellationRequested)
            {
                var registrySnapshot =
                    await GetRegistrySnapshotBestEffortAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var capacityDescriptor =
                    await GetCapacityDescriptorBestEffortAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                var registryReady =
                    IsRegistrySnapshotReady(
                        registrySnapshot,
                        out lastRegistryReason);

                var capacityReady =
                    IsCapacityDescriptorReady(
                        capacityDescriptor,
                        out lastCapacityReason);

                if (registryReady && capacityReady)
                {
                    _logger.LogInformation(
                        "Runtime readiness satisfied before shared queue pump start. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, RegistryStatus={RegistryStatus}, RegistryCanAcceptRun={RegistryCanAcceptRun}, RegistryAvailableRunSlots={RegistryAvailableRunSlots}, CapacityStatus={CapacityStatus}, CapacityCanAcceptRun={CapacityCanAcceptRun}, CapacityAvailableRunSlots={CapacityAvailableRunSlots}, DurationMs={DurationMs}.",
                        controlPlaneId,
                        runtimeInstanceId,
                        registrySnapshot?.Status,
                        registrySnapshot?.CanAcceptRun,
                        registrySnapshot?.AvailableRunSlots,
                        capacityDescriptor?.Status,
                        capacityDescriptor?.CanAcceptRun,
                        capacityDescriptor?.AvailableRunSlots,
                        (long)(DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds);

                    return;
                }

                if (deadlineUtc.HasValue &&
                    DateTimeOffset.UtcNow >= deadlineUtc.Value)
                {
                    _logger.LogWarning(
                        "Runtime readiness wait timed out before shared queue pump start. Continuing anyway. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, RegistryReady={RegistryReady}, CapacityReady={CapacityReady}, LastRegistryReason={LastRegistryReason}, LastCapacityReason={LastCapacityReason}, Timeout={Timeout}.",
                        controlPlaneId,
                        runtimeInstanceId,
                        registryReady,
                        capacityReady,
                        lastRegistryReason,
                        lastCapacityReason,
                        _options.RuntimeReadinessTimeout);

                    return;
                }

                _logger.LogDebug(
                    "Runtime readiness not satisfied before shared queue pump start. ControlPlaneId={ControlPlaneId}, RuntimeInstanceId={RuntimeInstanceId}, RegistryReady={RegistryReady}, CapacityReady={CapacityReady}, LastRegistryReason={LastRegistryReason}, LastCapacityReason={LastCapacityReason}.",
                    controlPlaneId,
                    runtimeInstanceId,
                    registryReady,
                    capacityReady,
                    lastRegistryReason,
                    lastCapacityReason);

                await Task.Delay(
                        pollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets the runtime instance registry snapshot without failing the background service.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The registry snapshot, or <c>null</c> when unavailable.</returns>
        private async Task<AiRuntimeInstanceSnapshot?> GetRegistrySnapshotBestEffortAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _runtimeInstanceRegistry
                    .GetAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Runtime readiness registry check failed. RuntimeInstanceId={RuntimeInstanceId}.",
                    runtimeInstanceId);

                return null;
            }
        }

        /// <summary>
        /// Gets the first available runtime instance capacity descriptor without failing
        /// the background service.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The capacity descriptor, or <c>null</c> when unavailable.</returns>
        private async Task<AiRuntimeInstanceCapacityDescriptor?> GetCapacityDescriptorBestEffortAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (_runtimeInstanceCapacityStores.Count == 0)
            {
                return null;
            }

            foreach (var capacityStore in _runtimeInstanceCapacityStores)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var descriptor =
                        await capacityStore
                            .GetAsync(
                                runtimeInstanceId,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (descriptor is not null)
                    {
                        return descriptor;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        exception,
                        "Runtime readiness capacity check failed. RuntimeInstanceId={RuntimeInstanceId}, CapacityStoreType={CapacityStoreType}.",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName);
                }
            }

            return null;
        }

        /// <summary>
        /// Determines whether a runtime instance registry snapshot is ready for shared queue pumping.
        /// </summary>
        /// <param name="snapshot">The registry snapshot.</param>
        /// <param name="reason">The reason when the snapshot is not ready.</param>
        /// <returns><c>true</c> when the registry snapshot is ready; otherwise, <c>false</c>.</returns>
        private static bool IsRegistrySnapshotReady(
            AiRuntimeInstanceSnapshot? snapshot,
            out string reason)
        {
            if (snapshot is null)
            {
                reason = "Registry snapshot is missing.";
                return false;
            }

            if (snapshot.Status != AiRuntimeInstanceStatus.Ready)
            {
                reason = $"Registry status is '{snapshot.Status}'.";
                return false;
            }

            if (snapshot.IsQueuePaused)
            {
                reason = "Registry queue is paused.";
                return false;
            }

            if (!snapshot.CanAcceptRun)
            {
                reason = "Registry snapshot cannot accept run.";
                return false;
            }

            reason = "Registry snapshot is ready.";
            return true;
        }

        /// <summary>
        /// Determines whether a runtime instance capacity descriptor is ready for shared queue pumping.
        /// </summary>
        /// <param name="descriptor">The capacity descriptor.</param>
        /// <param name="reason">The reason when the descriptor is not ready.</param>
        /// <returns><c>true</c> when the capacity descriptor is ready; otherwise, <c>false</c>.</returns>
        private bool IsCapacityDescriptorReady(
            AiRuntimeInstanceCapacityDescriptor? descriptor,
            out string reason)
        {
            if (_runtimeInstanceCapacityStores.Count == 0)
            {
                reason = "No runtime instance capacity store is registered.";
                return false;
            }

            if (descriptor is null)
            {
                reason = "Capacity descriptor is missing.";
                return false;
            }

            if (descriptor.Status != AiRuntimeInstanceStatus.Ready)
            {
                reason = $"Capacity status is '{descriptor.Status}'.";
                return false;
            }

            if (descriptor.IsQueuePaused)
            {
                reason = "Capacity queue is paused.";
                return false;
            }

            if (!descriptor.CanAcceptRun)
            {
                reason = "Capacity descriptor cannot accept run.";
                return false;
            }

            reason = "Capacity descriptor is ready.";
            return true;
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
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Metadata = _options.Metadata,
                            Source = "shared-queue-background-service",
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
            return Task.Delay(
                NormalizeDelay(delay),
                cancellationToken);
        }

        /// <summary>
        /// Normalizes a delay value to a safe positive delay.
        /// </summary>
        /// <param name="delay">The configured delay.</param>
        /// <returns>A safe delay.</returns>
        private static TimeSpan NormalizeDelay(
            TimeSpan delay)
        {
            return delay <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : delay;
        }
    }
}