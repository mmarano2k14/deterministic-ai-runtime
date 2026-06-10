using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Registers the current runtime instance and periodically publishes heartbeats.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Makes the current runtime instance visible to MCP tools, dashboards,
    ///   shared admission, autoscaling, diagnostics, and future Kubernetes controllers.
    /// - Publishes periodic heartbeat snapshots for runtime liveness and capacity tracking.
    /// - Unregisters the runtime instance during shutdown.
    ///
    /// IMPORTANT:
    /// - This service is provider-neutral.
    /// - Environment-specific metadata comes from <see cref="IAiRuntimeEnvironmentProvider"/>.
    /// - This service does not dispatch runs and does not execute DAG steps.
    /// </remarks>
    public sealed class AiRuntimeInstanceRegistrationHostedService : BackgroundService
    {
        private readonly IAiRuntimeInstanceRegistry registry;
        private readonly IAiRuntimeEnvironmentProvider environmentProvider;
        private readonly IAiRuntimePipelineBackgroundController controller;
        private readonly IReadOnlyCollection<IAiRuntimeInstanceCapacityStore> capacityStores;
        private readonly AiRuntimeInstanceRegistrationOptions options;
        private readonly ILogger<AiRuntimeInstanceRegistrationHostedService> logger;

        private string? runtimeInstanceId;
        private int stopRequested;

        public AiRuntimeInstanceRegistrationHostedService(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeEnvironmentProvider environmentProvider,
            IAiRuntimePipelineBackgroundController controller,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IOptions<AiRuntimeInstanceRegistrationOptions> options,
            ILogger<AiRuntimeInstanceRegistrationHostedService> logger)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            this.capacityStores = capacityStores?.ToArray()
                ?? throw new ArgumentNullException(nameof(capacityStores));
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            SafeLogInformation(
                "Runtime capacity stores resolved. Count={StoreCount}, Stores={Stores}",
                this.capacityStores.Count,
                string.Join(",", this.capacityStores.Select(store => store.GetType().FullName)));
        }

        /// <inheritdoc />
        public override async Task StartAsync(
            CancellationToken cancellationToken)
        {
            if (!options.Enabled)
            {
                SafeLogInformation(
                    "Runtime instance registration is disabled.");

                return;
            }

            SafeLogInformation(
                "Runtime instance registration service starting. RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                registry.GetType().FullName,
                registry.GetHashCode());

            await RegisterRuntimeInstanceAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            await base.StartAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PublishHeartbeatAsync(
                            stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to publish runtime instance heartbeat. RuntimeInstanceId={RuntimeInstanceId}",
                        runtimeInstanceId);
                }

                try
                {
                    await Task.Delay(
                            options.HeartbeatInterval,
                            stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 1)
            {
                SafeLogInformation(
                    "Runtime instance registration stop skipped. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                    runtimeInstanceId,
                    "AlreadyStoppedOrStopping");

                return;
            }

            try
            {
                try
                {
                    await UnregisterRuntimeInstanceAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SafeLogWarning(
                        "Runtime instance registration stop cancelled. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                        runtimeInstanceId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration stop ignored. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        runtimeInstanceId,
                        "DisposedDuringShutdown",
                        exception.Message);
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to unregister runtime instance during shutdown. RuntimeInstanceId={RuntimeInstanceId}",
                        runtimeInstanceId);
                }
            }
            finally
            {
                try
                {
                    await base.StopAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SafeLogWarning(
                        "Runtime instance registration base stop cancelled. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}",
                        runtimeInstanceId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration base stop ignored. RuntimeInstanceId={RuntimeInstanceId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        runtimeInstanceId,
                        "DisposedDuringShutdown",
                        exception.Message);
                }
            }
        }

        /// <summary>
        /// Registers the current runtime instance in the runtime instance registry.
        /// </summary>
        private async Task RegisterRuntimeInstanceAsync(
            CancellationToken cancellationToken)
        {
            var environment =
                await environmentProvider
                    .GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);

            runtimeInstanceId =
                ResolveRuntimeInstanceId(environment);

            SafeLogInformation(
                "Runtime instance id resolved. RuntimeInstanceId={RuntimeInstanceId}, OptionsRuntimeInstanceId={OptionsRuntimeInstanceId}, EnvironmentRuntimeInstanceId={EnvironmentRuntimeInstanceId}, HostName={HostName}, ProcessId={ProcessId}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                options.RuntimeInstanceId,
                environment.RuntimeInstanceId,
                environment.HostName,
                environment.ProcessId,
                environment.HostId,
                environment.RuntimeId,
                environment.ControlPlaneHostId,
                registry.GetType().FullName,
                registry.GetHashCode());

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            SafeLogInformation(
                "Runtime instance queue state resolved. RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, QueueCapacity={QueueCapacity}, MaxConcurrentRuns={MaxConcurrentRuns}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}",
                runtimeInstanceId,
                options.Role,
                queueState.QueuedRunCount,
                queueState.RunningRunCount,
                queueState.ActiveRunCount,
                queueState.AvailableRunSlots,
                effectiveCapacity.AvailableRunSlots,
                queueState.QueueCapacity,
                queueState.MaxConcurrentRuns,
                queueState.IsPaused,
                queueState.CanAcceptRun,
                effectiveCapacity.CanAcceptRun,
                effectiveCapacity.QueueHasCapacity);

            var registration =
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    HostName = environment.HostName,
                    ProcessId = environment.ProcessId,
                    HostId = environment.HostId,
                    RuntimeId = environment.RuntimeId,
                    ControlPlaneHostId = environment.ControlPlaneHostId,
                    WorkerCount = options.WorkerCount,
                    QueueCapacity = options.QueueCapacity ?? queueState.QueueCapacity,
                    MaxConcurrentRuns = options.MaxConcurrentRuns ?? queueState.MaxConcurrentRuns,
                    RuntimeVersion = options.RuntimeVersion,
                    Role = options.Role,
                    Metadata = MergeMetadata(
                        options.Metadata,
                        options.ProviderMetadata,
                        environment.ProviderMetadata,
                        new Dictionary<string, string>
                        {
                            ["provider"] = options.ProviderName ?? environment.ProviderName
                        })
                };

            SafeLogInformation(
                "Runtime instance capacity publication before registry registration started. RuntimeInstanceId={RuntimeInstanceId}",
                runtimeInstanceId);

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    registration.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance registration started. RuntimeInstanceId={RuntimeInstanceId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                registry.GetType().FullName,
                registry.GetHashCode());

            var snapshot =
                await registry
                    .RegisterAsync(registration, cancellationToken)
                    .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance registered. RuntimeInstanceId={RuntimeInstanceId}, Status={Status}, Provider={Provider}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                snapshot.RuntimeInstanceId,
                snapshot.Status,
                options.ProviderName ?? environment.ProviderName,
                snapshot.HostId,
                snapshot.RuntimeId,
                snapshot.ControlPlaneHostId,
                registry.GetType().FullName,
                registry.GetHashCode());
        }

        /// <summary>
        /// Publishes a heartbeat for the current runtime instance.
        /// </summary>
        private async Task PublishHeartbeatAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                SafeLogWarning(
                    "Runtime instance heartbeat skipped because RuntimeInstanceId is empty.");

                return;
            }

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            SafeLogInformation(
                "Runtime instance heartbeat started. RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                options.Role,
                queueState.QueuedRunCount,
                queueState.RunningRunCount,
                queueState.ActiveRunCount,
                queueState.AvailableRunSlots,
                effectiveCapacity.AvailableRunSlots,
                queueState.IsPaused,
                queueState.CanAcceptRun,
                effectiveCapacity.CanAcceptRun,
                effectiveCapacity.QueueHasCapacity,
                registry.GetType().FullName,
                registry.GetHashCode());

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    options.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshot =
                await registry
                    .HeartbeatAsync(
                        runtimeInstanceId,
                        queueState.QueuedRunCount,
                        queueState.RunningRunCount,
                        queueState.ActiveRunCount,
                        effectiveCapacity.AvailableRunSlots,
                        effectiveCapacity.ActiveWorkerCount,
                        effectiveCapacity.AvailableWorkerCount,
                        effectiveCapacity.MaxLocalWorkersPerExecution,
                        queueState.IsPaused,
                        effectiveCapacity.CanAcceptRun,
                        AiRuntimeInstanceStatus.Ready,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (snapshot is null)
            {
                SafeLogWarning(
                    "Runtime instance heartbeat ignored because instance is not registered. RuntimeInstanceId={RuntimeInstanceId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    runtimeInstanceId,
                    registry.GetType().FullName,
                    registry.GetHashCode());
            }
            else
            {
                SafeLogInformation(
                    "Runtime instance heartbeat succeeded. RuntimeInstanceId={RuntimeInstanceId}, Status={Status}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, CanAcceptRun={CanAcceptRun}, AvailableRunSlots={AvailableRunSlots}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    snapshot.RuntimeInstanceId,
                    snapshot.Status,
                    snapshot.HostId,
                    snapshot.RuntimeId,
                    snapshot.ControlPlaneHostId,
                    snapshot.CanAcceptRun,
                    snapshot.AvailableRunSlots,
                    registry.GetType().FullName,
                    registry.GetHashCode());
            }
        }

        /// <summary>
        /// Unregisters the current runtime instance from the runtime instance registry.
        /// </summary>
        private async Task UnregisterRuntimeInstanceAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                SafeLogWarning(
                    "Runtime instance unregister skipped because RuntimeInstanceId is empty.");

                return;
            }

            SafeLogInformation(
                "Runtime instance unregister started. RuntimeInstanceId={RuntimeInstanceId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                registry.GetType().FullName,
                registry.GetHashCode());

            var snapshot =
                await registry
                    .UnregisterAsync(runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

            await RemoveCapacityDescriptorAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance unregistered. RuntimeInstanceId={RuntimeInstanceId}, Status={Status}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                snapshot?.Status,
                snapshot?.HostId,
                snapshot?.RuntimeId,
                snapshot?.ControlPlaneHostId,
                registry.GetType().FullName,
                registry.GetHashCode());
        }

        /// <summary>
        /// Publishes the current runtime capacity descriptor to all configured capacity stores.
        /// </summary>
        private async Task PublishCapacityDescriptorAsync(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status,
            AiRuntimePipelineQueueState queueState,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (capacityStores.Count == 0)
            {
                return;
            }

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    status,
                    queueState);

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = options.Role,
                    Status = status,
                    WorkerCount = effectiveCapacity.WorkerCount,
                    ActiveWorkerCount = effectiveCapacity.ActiveWorkerCount,
                    AvailableWorkerCount = effectiveCapacity.AvailableWorkerCount,
                    MaxWorkersPerRun = effectiveCapacity.MaxLocalWorkersPerExecution,
                    MinWorkersRequiredPerRun = 1,
                    QueuedRunCount = queueState.QueuedRunCount,
                    RunningRunCount = queueState.RunningRunCount,
                    ActiveRunCount = queueState.ActiveRunCount,
                    MaxConcurrentRuns = queueState.MaxConcurrentRuns,
                    MaxRunSlots = queueState.MaxConcurrentRuns,
                    AvailableRunSlots = effectiveCapacity.AvailableRunSlots,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = effectiveCapacity.AvailableRunSlots,
                    IsQueuePaused = queueState.IsPaused,
                    CanAcceptRun = effectiveCapacity.CanAcceptRun,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = metadata
                };

            SafeLogInformation(
                "Runtime instance capacity descriptor publishing. RuntimeInstanceId={RuntimeInstanceId}, Role={Role}, Status={Status}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueCapacity={QueueCapacity}, QueueHasCapacity={QueueHasCapacity}, AvailableRunSlots={AvailableRunSlots}, AvailableWorkerCount={AvailableWorkerCount}, CanAcceptRun={CanAcceptRun}, IsQueuePaused={IsQueuePaused}, StoreCount={StoreCount}",
                runtimeInstanceId,
                descriptor.Role,
                descriptor.Status,
                descriptor.QueuedRunCount,
                descriptor.RunningRunCount,
                descriptor.ActiveRunCount,
                queueState.QueueCapacity,
                effectiveCapacity.QueueHasCapacity,
                descriptor.AvailableRunSlots,
                descriptor.AvailableWorkerCount,
                descriptor.CanAcceptRun,
                descriptor.IsQueuePaused,
                capacityStores.Count);

            foreach (var capacityStore in capacityStores)
            {
                try
                {
                    await capacityStore
                        .PublishAsync(descriptor, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to publish runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName);
                }
            }
        }

        /// <summary>
        /// Removes the runtime capacity descriptor from all configured capacity stores.
        /// </summary>
        private async Task RemoveCapacityDescriptorAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (capacityStores.Count == 0)
            {
                return;
            }

            foreach (var capacityStore in capacityStores)
            {
                try
                {
                    await capacityStore
                        .RemoveAsync(runtimeInstanceId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to remove runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName);
                }
            }
        }

        /// <summary>
        /// Creates the effective capacity values that must be published to the registry
        /// and capacity stores.
        /// </summary>
        /// <remarks>
        /// Queue-first semantics:
        /// - <c>AvailableRunSlots</c> represents immediate execution capacity.
        /// - <c>CanAcceptRun</c> represents whether the local queue can accept another run.
        /// - A runtime can accept a run even when all workers are currently busy, as long
        ///   as the local queue has capacity.
        /// - Non-runtime roles must never be dispatchable.
        /// </remarks>
        /// <param name="status">The runtime instance status.</param>
        /// <param name="queueState">The local runtime queue state.</param>
        /// <returns>The effective runtime capacity.</returns>
        private EffectiveRuntimeCapacity CreateEffectiveCapacity(
            AiRuntimeInstanceStatus status,
            AiRuntimePipelineQueueState queueState)
        {
            var role =
                options.Role;

            var workerCount =
                queueState.WorkerCount ?? options.WorkerCount;

            var activeWorkerCount =
                queueState.ActiveWorkerCount ?? 0;

            var availableWorkerCount =
                queueState.AvailableWorkerCount ?? Math.Max(
                    0,
                    workerCount - activeWorkerCount);

            var queueHasCapacity =
                queueState.QueueCapacity is null ||
                queueState.QueuedRunCount < queueState.QueueCapacity.Value;

            var isRuntime =
                role == AiRuntimeInstanceRole.Runtime;

            if (!isRuntime)
            {
                return new EffectiveRuntimeCapacity(
                    WorkerCount: 0,
                    ActiveWorkerCount: 0,
                    AvailableWorkerCount: 0,
                    AvailableRunSlots: 0,
                    MaxLocalWorkersPerExecution: queueState.MaxLocalWorkersPerExecution,
                    QueueHasCapacity: queueHasCapacity,
                    CanAcceptRun: false);
            }

            var availableRunSlots =
                queueState.AvailableRunSlots;

            var canAcceptRun =
                status == AiRuntimeInstanceStatus.Ready &&
                !queueState.IsPaused &&
                queueHasCapacity;

            return new EffectiveRuntimeCapacity(
                WorkerCount: workerCount,
                ActiveWorkerCount: activeWorkerCount,
                AvailableWorkerCount: availableWorkerCount,
                AvailableRunSlots: availableRunSlots,
                MaxLocalWorkersPerExecution: queueState.MaxLocalWorkersPerExecution,
                QueueHasCapacity: queueHasCapacity,
                CanAcceptRun: canAcceptRun);
        }

        /// <summary>
        /// Resolves the runtime instance identifier from options or environment.
        /// </summary>
        private string ResolveRuntimeInstanceId(
            AiRuntimeEnvironmentSnapshot environment)
        {
            if (!string.IsNullOrWhiteSpace(options.RuntimeInstanceId))
            {
                return options.RuntimeInstanceId;
            }

            if (!string.IsNullOrWhiteSpace(environment.RuntimeInstanceId))
            {
                return environment.RuntimeInstanceId;
            }

            if (!string.IsNullOrWhiteSpace(environment.HostId) &&
                !string.IsNullOrWhiteSpace(environment.RuntimeId))
            {
                return $"{environment.HostId}:{environment.RuntimeId}";
            }

            return $"runtime:{environment.HostName ?? "unknown"}:{environment.ProcessId?.ToString() ?? Guid.NewGuid().ToString("N")}";
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
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

        private void SafeLogInformation(
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogInformation(
                    message,
                    args);
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private void SafeLogWarning(
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogWarning(
                    message,
                    args);
            }
            catch (AggregateException aggregateException)
                when (aggregateException.InnerExceptions.Any(inner =>
                    inner is ObjectDisposedException or InvalidOperationException))
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (InvalidOperationException)
            {
                // Logger infrastructure may already be unavailable during shutdown.
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private void SafeLogError(
            Exception exception,
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogError(
                    exception,
                    message,
                    args);
            }
            catch (AggregateException aggregateException)
                when (aggregateException.InnerExceptions.Any(inner =>
                    inner is ObjectDisposedException or InvalidOperationException))
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (InvalidOperationException)
            {
                // Logger infrastructure may already be unavailable during shutdown.
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private sealed record EffectiveRuntimeCapacity(
            int WorkerCount,
            int ActiveWorkerCount,
            int AvailableWorkerCount,
            int? AvailableRunSlots,
            int? MaxLocalWorkersPerExecution,
            bool QueueHasCapacity,
            bool CanAcceptRun);
    }
}