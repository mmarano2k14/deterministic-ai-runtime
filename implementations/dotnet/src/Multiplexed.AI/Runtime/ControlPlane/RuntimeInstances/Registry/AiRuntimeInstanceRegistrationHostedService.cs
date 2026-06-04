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

            Console.WriteLine(
                $"[RUNTIME CAPACITY] STORES RESOLVED Count='{this.capacityStores.Count}' Stores='{string.Join(",", this.capacityStores.Select(store => store.GetType().FullName))}'");
        }

        /// <inheritdoc />
        public override async Task StartAsync(
            CancellationToken cancellationToken)
        {
            if (!options.Enabled)
            {
                logger.LogInformation(
                    "Runtime instance registration is disabled.");

                Console.WriteLine(
                    "[RUNTIME REGISTRATION] DISABLED");

                return;
            }

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] START SERVICE RegistryType='{registry.GetType().FullName}' RegistryHash='{registry.GetHashCode()}'");

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
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to publish runtime instance heartbeat.");

                    Console.WriteLine(
                        $"[RUNTIME REGISTRATION] HEARTBEAT EXCEPTION RuntimeInstanceId='{runtimeInstanceId}' Exception='{ex}'");
                }

                await Task.Delay(
                        options.HeartbeatInterval,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public override async Task StopAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref stopRequested, 1) == 1)
            {
                Console.WriteLine(
                    $"[RUNTIME REGISTRATION] STOP SKIPPED RuntimeInstanceId='{runtimeInstanceId}' Reason='AlreadyStoppedOrStopping'");

                return;
            }

            try
            {
                await UnregisterRuntimeInstanceAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await base.StopAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
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

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] RESOLVED RuntimeInstanceId='{runtimeInstanceId}' " +
                $"OptionsRuntimeInstanceId='{options.RuntimeInstanceId}' " +
                $"EnvironmentRuntimeInstanceId='{environment.RuntimeInstanceId}' " +
                $"HostName='{environment.HostName}' " +
                $"ProcessId='{environment.ProcessId}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] QUEUE STATE RuntimeInstanceId='{runtimeInstanceId}' " +
                $"QueuedRunCount='{queueState.QueuedRunCount}' " +
                $"RunningRunCount='{queueState.RunningRunCount}' " +
                $"ActiveRunCount='{queueState.ActiveRunCount}' " +
                $"AvailableRunSlots='{queueState.AvailableRunSlots}' " +
                $"QueueCapacity='{queueState.QueueCapacity}' " +
                $"MaxConcurrentRuns='{queueState.MaxConcurrentRuns}' " +
                $"IsPaused='{queueState.IsPaused}' " +
                $"CanAcceptRun='{queueState.CanAcceptRun}'");

            var registration =
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    HostName = environment.HostName,
                    ProcessId = environment.ProcessId,
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

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] REGISTER START RuntimeInstanceId='{runtimeInstanceId}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            var snapshot =
                await registry
                    .RegisterAsync(registration, cancellationToken)
                    .ConfigureAwait(false);

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    snapshot.Status,
                    queueState,
                    registration.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] REGISTER SUCCESS RuntimeInstanceId='{snapshot.RuntimeInstanceId}' " +
                $"Status='{snapshot.Status}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            logger.LogInformation(
                "Runtime instance registered. RuntimeInstanceId={RuntimeInstanceId}, Status={Status}, Provider={Provider}",
                snapshot.RuntimeInstanceId,
                snapshot.Status,
                options.ProviderName ?? environment.ProviderName);
        }

        /// <summary>
        /// Publishes a heartbeat for the current runtime instance.
        /// </summary>
        private async Task PublishHeartbeatAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(runtimeInstanceId))
            {
                Console.WriteLine(
                    "[RUNTIME REGISTRATION] HEARTBEAT SKIPPED RuntimeInstanceId is empty.");

                return;
            }

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] HEARTBEAT RuntimeInstanceId='{runtimeInstanceId}' " +
                $"QueuedRunCount='{queueState.QueuedRunCount}' " +
                $"RunningRunCount='{queueState.RunningRunCount}' " +
                $"ActiveRunCount='{queueState.ActiveRunCount}' " +
                $"AvailableRunSlots='{queueState.AvailableRunSlots}' " +
                $"IsPaused='{queueState.IsPaused}' " +
                $"CanAcceptRun='{queueState.CanAcceptRun}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            var snapshot =
                await registry
                    .HeartbeatAsync(
                        runtimeInstanceId,
                        queueState.QueuedRunCount,
                        queueState.RunningRunCount,
                        queueState.ActiveRunCount,
                        queueState.AvailableRunSlots,
                        queueState.IsPaused,
                        queueState.CanAcceptRun,
                        AiRuntimeInstanceStatus.Ready,
                        cancellationToken)
                    .ConfigureAwait(false);

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    snapshot?.Status ?? AiRuntimeInstanceStatus.Ready,
                    queueState,
                    options.Metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            if (snapshot is null)
            {
                logger.LogWarning(
                    "Runtime instance heartbeat ignored because instance is not registered. RuntimeInstanceId={RuntimeInstanceId}",
                    runtimeInstanceId);

                Console.WriteLine(
                    $"[RUNTIME REGISTRATION] HEARTBEAT IGNORED RuntimeInstanceId='{runtimeInstanceId}' " +
                    $"RegistryType='{registry.GetType().FullName}' " +
                    $"RegistryHash='{registry.GetHashCode()}'");
            }
            else
            {
                Console.WriteLine(
                    $"[RUNTIME REGISTRATION] HEARTBEAT SUCCESS RuntimeInstanceId='{snapshot.RuntimeInstanceId}' " +
                    $"Status='{snapshot.Status}' " +
                    $"RegistryType='{registry.GetType().FullName}' " +
                    $"RegistryHash='{registry.GetHashCode()}'");
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
                Console.WriteLine(
                    "[RUNTIME REGISTRATION] UNREGISTER SKIPPED RuntimeInstanceId is empty.");

                return;
            }

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] UNREGISTER START RuntimeInstanceId='{runtimeInstanceId}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            var snapshot =
                await registry
                    .UnregisterAsync(runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

            await RemoveCapacityDescriptorAsync(
                    runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine(
                $"[RUNTIME REGISTRATION] UNREGISTER SUCCESS RuntimeInstanceId='{runtimeInstanceId}' " +
                $"Status='{snapshot?.Status}' " +
                $"RegistryType='{registry.GetType().FullName}' " +
                $"RegistryHash='{registry.GetHashCode()}'");

            logger.LogInformation(
                "Runtime instance unregistered. RuntimeInstanceId={RuntimeInstanceId}, Status={Status}",
                runtimeInstanceId,
                snapshot?.Status);
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

            var role =
                options.Role;

            var availableRunSlots =
                role == AiRuntimeInstanceRole.Runtime
                    ? queueState.AvailableRunSlots
                    : 0;

            var canAcceptRun =
                role == AiRuntimeInstanceRole.Runtime &&
                queueState.CanAcceptRun;

            var workerCount =
                options.WorkerCount;

            var activeWorkerCount =
                0;

            var availableWorkerCount =
                workerCount;

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    Role = role,
                    Status = status,
                    WorkerCount = workerCount,
                    ActiveWorkerCount = activeWorkerCount,
                    AvailableWorkerCount = availableWorkerCount,
                    MaxWorkersPerRun = null,
                    MinWorkersRequiredPerRun = 1,
                    QueuedRunCount = queueState.QueuedRunCount,
                    RunningRunCount = queueState.RunningRunCount,
                    ActiveRunCount = queueState.ActiveRunCount,
                    MaxConcurrentRuns = queueState.MaxConcurrentRuns,
                    MaxRunSlots = queueState.MaxConcurrentRuns,
                    AvailableRunSlots = availableRunSlots,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = availableRunSlots,
                    IsQueuePaused = queueState.IsPaused,
                    CanAcceptRun = canAcceptRun,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = metadata
                };

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
                    logger.LogError(
                        exception,
                        "Failed to publish runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName);

                    Console.WriteLine(
                        $"[RUNTIME CAPACITY] PUBLISH FAILED RuntimeInstanceId='{runtimeInstanceId}' StoreType='{capacityStore.GetType().FullName}' Exception='{exception.Message}'");
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
                    logger.LogError(
                        exception,
                        "Failed to remove runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName);

                    Console.WriteLine(
                        $"[RUNTIME CAPACITY] REMOVE FAILED RuntimeInstanceId='{runtimeInstanceId}' StoreType='{capacityStore.GetType().FullName}' Exception='{exception.Message}'");
                }
            }
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
    }
}