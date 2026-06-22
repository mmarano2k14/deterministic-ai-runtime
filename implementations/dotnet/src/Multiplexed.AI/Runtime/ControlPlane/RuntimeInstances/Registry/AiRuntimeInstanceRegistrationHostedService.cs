using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
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
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly IReadOnlyCollection<IAiRuntimeInstanceCapacityStore> capacityStores;
        private readonly AiRuntimeInstanceRegistrationOptions options;
        private readonly ILogger<AiRuntimeInstanceRegistrationHostedService> logger;

        private string? runtimeInstanceId;
        private string? controlPlaneId;
        private string? controlPlaneHostId;
        private IReadOnlyDictionary<string, string> runtimeMetadata =
            new Dictionary<string, string>();
        private int stopRequested;

        public AiRuntimeInstanceRegistrationHostedService(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeEnvironmentProvider environmentProvider,
            IAiRuntimePipelineBackgroundController controller,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IOptions<AiRuntimeInstanceRegistrationOptions> options,
            ILogger<AiRuntimeInstanceRegistrationHostedService> logger)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
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
                        "Failed to publish runtime instance heartbeat. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId);
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
                    "Runtime instance registration stop skipped. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}",
                    runtimeInstanceId,
                    controlPlaneId,
                    controlPlaneHostId,
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
                        "Runtime instance registration stop cancelled. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration stop ignored. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
                        "DisposedDuringShutdown",
                        exception.Message);
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to unregister runtime instance during shutdown. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId);
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
                        "Runtime instance registration base stop cancelled. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration base stop ignored. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
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
            controlPlaneId =
                await controlPlaneIdResolver
                    .ResolveAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane identifier cannot be null or empty.");
            }

            var environment =
                await environmentProvider
                    .GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);

            controlPlaneHostId =
                environment.ControlPlaneHostId;

            runtimeInstanceId =
                ResolveRuntimeInstanceId(environment);

            var providerName =
                ResolveProviderName(environment);

            runtimeMetadata =
                MergeMetadata(
                    options.Metadata,
                    options.ProviderMetadata,
                    environment.ProviderMetadata,
                    new Dictionary<string, string>
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName,
                        ["provider"] = providerName,
                        ["controlPlaneId"] = controlPlaneId,
                        ["controlPlaneHostId"] = controlPlaneHostId ?? string.Empty
                    });

            SafeLogInformation(
                "Runtime instance id resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, OptionsRuntimeInstanceId={OptionsRuntimeInstanceId}, EnvironmentRuntimeInstanceId={EnvironmentRuntimeInstanceId}, HostName={HostName}, ProcessId={ProcessId}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, ProviderName={ProviderName}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                controlPlaneId,
                options.RuntimeInstanceId,
                environment.RuntimeInstanceId,
                environment.HostName,
                environment.ProcessId,
                environment.HostId,
                environment.RuntimeId,
                controlPlaneHostId,
                providerName,
                registry.GetType().FullName,
                registry.GetHashCode());

            SafeLogInformation(
                "Runtime instance registration metadata resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, ProviderName={ProviderName}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, Metadata={Metadata}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId,
                options.Role,
                providerName,
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                FormatMetadata(runtimeMetadata));

            var queueState =
                await controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            SafeLogInformation(
                "Runtime instance queue state resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, QueueCapacity={QueueCapacity}, MaxConcurrentRuns={MaxConcurrentRuns}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId,
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
                    ControlPlaneId = controlPlaneId,
                    HostName = environment.HostName,
                    ProcessId = environment.ProcessId,
                    HostId = environment.HostId,
                    RuntimeId = environment.RuntimeId,
                    ControlPlaneHostId = controlPlaneHostId,
                    WorkerCount = options.WorkerCount,
                    QueueCapacity = options.QueueCapacity ?? queueState.QueueCapacity,
                    MaxConcurrentRuns = options.MaxConcurrentRuns ?? queueState.MaxConcurrentRuns,
                    RuntimeVersion = options.RuntimeVersion,
                    Role = options.Role,
                    Metadata = runtimeMetadata
                };

            SafeLogInformation(
                "Runtime instance capacity publication before registry registration started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId);

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    runtimeMetadata,
                    cancellationToken)
                .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance registration started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId,
                registry.GetType().FullName,
                registry.GetHashCode());

            var snapshot =
                await registry
                    .RegisterAsync(registration, cancellationToken)
                    .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance registered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, Status={Status}, Provider={Provider}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, Metadata={Metadata}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                snapshot.RuntimeInstanceId,
                snapshot.ControlPlaneId,
                snapshot.Status,
                providerName,
                snapshot.HostId,
                snapshot.RuntimeId,
                snapshot.ControlPlaneHostId,
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                FormatMetadata(snapshot.Metadata),
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

            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                SafeLogWarning(
                    "Runtime instance heartbeat skipped because ControlPlaneId is empty. RuntimeInstanceId={RuntimeInstanceId}",
                    runtimeInstanceId);

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
                "Runtime instance heartbeat started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId,
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
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                registry.GetType().FullName,
                registry.GetHashCode());

            await PublishCapacityDescriptorAsync(
                    runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    runtimeMetadata,
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
                    "Runtime instance heartbeat ignored because instance is not registered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    runtimeInstanceId,
                    controlPlaneId,
                    controlPlaneHostId,
                    registry.GetType().FullName,
                    registry.GetHashCode());
            }
            else
            {
                SafeLogInformation(
                    "Runtime instance heartbeat succeeded. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, Status={Status}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, CanAcceptRun={CanAcceptRun}, AvailableRunSlots={AvailableRunSlots}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    snapshot.RuntimeInstanceId,
                    snapshot.ControlPlaneId,
                    snapshot.Status,
                    snapshot.HostId,
                    snapshot.RuntimeId,
                    snapshot.ControlPlaneHostId,
                    snapshot.CanAcceptRun,
                    snapshot.AvailableRunSlots,
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
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
                "Runtime instance unregister started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                controlPlaneId,
                controlPlaneHostId,
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
                "Runtime instance unregistered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, Status={Status}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                runtimeInstanceId,
                snapshot?.ControlPlaneId,
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

            var descriptorMetadata =
                EnsureProviderMetadata(
                    metadata);

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    ControlPlaneId = controlPlaneId,
                    ControlPlaneHostId = controlPlaneHostId,
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
                    Metadata = descriptorMetadata
                };

            SafeLogInformation(
                "Runtime instance capacity metadata resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, Provider={Provider}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, Metadata={Metadata}",
                runtimeInstanceId,
                descriptor.ControlPlaneId,
                descriptor.ControlPlaneHostId,
                descriptor.Role,
                descriptorMetadata.TryGetValue(AiRuntimeInstanceProviderMetadataKeys.ProviderName, out var metadataProviderName)
                    ? metadataProviderName
                    : descriptorMetadata.TryGetValue("provider", out var metadataProviderAlias)
                        ? metadataProviderAlias
                        : "(unknown)",
                GetMetadataValue(descriptorMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                GetMetadataValue(descriptorMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                GetMetadataValue(descriptorMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(descriptorMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(descriptorMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                FormatMetadata(descriptorMetadata));

            SafeLogInformation(
                "Runtime instance capacity descriptor publishing. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, Status={Status}, Provider={Provider}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueCapacity={QueueCapacity}, QueueHasCapacity={QueueHasCapacity}, AvailableRunSlots={AvailableRunSlots}, AvailableWorkerCount={AvailableWorkerCount}, CanAcceptRun={CanAcceptRun}, IsQueuePaused={IsQueuePaused}, StoreCount={StoreCount}",
                runtimeInstanceId,
                descriptor.ControlPlaneId,
                descriptor.ControlPlaneHostId,
                descriptor.Role,
                descriptor.Status,
                descriptorMetadata.TryGetValue(AiRuntimeInstanceProviderMetadataKeys.ProviderName, out var providerName)
                    ? providerName
                    : descriptorMetadata.TryGetValue("provider", out var providerAlias)
                        ? providerAlias
                        : "(unknown)",
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
                        "Failed to publish runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
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
                        "Failed to remove runtime instance capacity descriptor. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, StoreType={StoreType}",
                        runtimeInstanceId,
                        controlPlaneId,
                        controlPlaneHostId,
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
        /// Resolves the logical provider name for the current runtime instance.
        /// </summary>
        /// <param name="environment">The runtime environment snapshot.</param>
        /// <returns>The logical provider name.</returns>
        private string ResolveProviderName(
            AiRuntimeEnvironmentSnapshot environment)
        {
            var providerName =
                options.ProviderName ?? environment.ProviderName;

            if (string.IsNullOrWhiteSpace(providerName))
            {
                return "local";
            }

            return providerName.Trim();
        }

        /// <summary>
        /// Ensures that metadata contains both the canonical provider key and the legacy provider alias.
        /// </summary>
        /// <param name="metadata">The source metadata.</param>
        /// <returns>The normalized metadata dictionary.</returns>
        private IReadOnlyDictionary<string, string> EnsureProviderMetadata(
            IReadOnlyDictionary<string, string> metadata)
        {
            var result =
                new Dictionary<string, string>(
                    metadata,
                    StringComparer.OrdinalIgnoreCase);

            var providerName =
                ResolveProviderNameFromMetadata(result);

            result[AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                providerName;

            result["provider"] =
                providerName;

            return result;
        }

        /// <summary>
        /// Resolves the provider name from metadata or registration options.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <returns>The resolved provider name.</returns>
        private string ResolveProviderNameFromMetadata(
            IReadOnlyDictionary<string, string> metadata)
        {
            if (metadata.TryGetValue(
                    AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                    out var providerName) &&
                !string.IsNullOrWhiteSpace(providerName))
            {
                return providerName.Trim();
            }

            if (metadata.TryGetValue(
                    "provider",
                    out var providerAlias) &&
                !string.IsNullOrWhiteSpace(providerAlias))
            {
                return providerAlias.Trim();
            }

            var configuredProviderName =
                options.ProviderName;

            if (!string.IsNullOrWhiteSpace(configuredProviderName))
            {
                return configuredProviderName.Trim();
            }

            return "local";
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            params IReadOnlyDictionary<string, string>[] sources)
        {
            var result =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

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

        private static string? GetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            string key)
        {
            if (metadata is null)
            {
                return null;
            }

            return metadata.TryGetValue(key, out var value)
                ? value
                : null;
        }

        private static string FormatMetadata(
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return "(empty)";
            }

            return string.Join(
                " | ",
                metadata
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}={pair.Value}"));
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