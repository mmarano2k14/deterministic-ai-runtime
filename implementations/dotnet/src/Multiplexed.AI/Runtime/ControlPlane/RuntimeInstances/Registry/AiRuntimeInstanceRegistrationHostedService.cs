using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;

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
    /// - Registry and capacity observability is intentionally provided by decorators around
    ///   <see cref="IAiRuntimeInstanceRegistry"/> and <see cref="IAiRuntimeInstanceCapacityStore"/>.
    /// - This keeps registration lifecycle logic independent from Redis, in-memory, logging,
    ///   tracing, ledger, and future Kubernetes-backed stores.
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
        private readonly IAiRuntimeLifecycleJournal lifecycleJournal;

        private string? poolId;
        private string? hostId;
        private string? runtimeInstanceId;
        private string? controlPlaneId;
        private string? controlPlaneHostId;
        private AiRuntimeInstanceRegistration? runtimeRegistration;
        private IReadOnlyDictionary<string, string> runtimeMetadata =
            new Dictionary<string, string>();
        private int stopRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceRegistrationHostedService"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="environmentProvider">The runtime environment provider.</param>
        /// <param name="controller">The runtime pipeline background controller.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane identifier resolver.</param>
        /// <param name="capacityStores">The runtime instance capacity stores.</param>
        /// <param name="options">The runtime instance registration options.</param>
        /// <param name="logger">The logger.</param>
        public AiRuntimeInstanceRegistrationHostedService(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeEnvironmentProvider environmentProvider,
            IAiRuntimePipelineBackgroundController controller,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IOptions<AiRuntimeInstanceRegistrationOptions> options,
            ILogger<AiRuntimeInstanceRegistrationHostedService> logger)
            : this(
                registry,
                environmentProvider,
                controller,
                controlPlaneIdResolver,
                capacityStores,
                options,
                logger,
                NoopAiRuntimeLifecycleJournal.Instance)
        {
        }

        /// <summary>
        /// Initializes a runtime registration service with durable lifecycle journaling.
        /// </summary>
        public AiRuntimeInstanceRegistrationHostedService(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeEnvironmentProvider environmentProvider,
            IAiRuntimePipelineBackgroundController controller,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IEnumerable<IAiRuntimeInstanceCapacityStore> capacityStores,
            IOptions<AiRuntimeInstanceRegistrationOptions> options,
            ILogger<AiRuntimeInstanceRegistrationHostedService> logger,
            IAiRuntimeLifecycleJournal lifecycleJournal)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.capacityStores = capacityStores?.ToArray()
                ?? throw new ArgumentNullException(nameof(capacityStores));
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.lifecycleJournal = lifecycleJournal ?? throw new ArgumentNullException(nameof(lifecycleJournal));

            SafeLogInformation(
                "Runtime capacity stores resolved. Count={StoreCount}, Stores={Stores}, RegistryType={RegistryType}",
                this.capacityStores.Count,
                string.Join(",", this.capacityStores.Select(store => store.GetType().FullName)),
                this.registry.GetType().FullName);
        }

        /// <inheritdoc />
        public override async Task StartAsync(
            CancellationToken cancellationToken)
        {

            SafeLogInformation(
                "Runtime instance registration options resolved. Enabled={Enabled}, RuntimeInstanceId={RuntimeInstanceId}, ProviderName={ProviderName}, Role={Role}",
                this.options.Enabled,
                this.options.RuntimeInstanceId,
                this.options.ProviderName,
                this.options.Role);

            if (!this.options.Enabled)
            {
                SafeLogInformation(
                    "Runtime instance registration is disabled.");

                return;
            }

            SafeLogInformation(
                "Runtime instance registration service starting. RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

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
            if (!this.options.Enabled)
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
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId);
                }

                try
                {
                    await Task.Delay(
                            this.options.HeartbeatInterval,
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
            if (Interlocked.Exchange(ref this.stopRequested, 1) == 1)
            {
                SafeLogInformation(
                    "Runtime instance registration stop skipped. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}",
                    this.runtimeInstanceId,
                    this.controlPlaneId,
                    this.controlPlaneHostId,
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
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration stop ignored. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        "DisposedDuringShutdown",
                        exception.Message);
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to unregister runtime instance during shutdown. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId);
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
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        "ShutdownCancellationRequested");
                }
                catch (ObjectDisposedException exception)
                {
                    SafeLogWarning(
                        "Runtime instance registration base stop ignored. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Reason={Reason}, ExceptionMessage={ExceptionMessage}",
                        this.runtimeInstanceId,
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        "DisposedDuringShutdown",
                        exception.Message);
                }
            }
        }

        /// <summary>
        /// Registers the current runtime instance in the runtime instance registry.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RegisterRuntimeInstanceAsync(
            CancellationToken cancellationToken)
        {
            var environment =
                await this.environmentProvider
                    .GetSnapshotAsync(cancellationToken)
                    .ConfigureAwait(false);

            var registrationIdentity =
                AiRuntimeInstanceRegistrationIdentityResolver.Resolve(
                    this.options.PoolId,
                    this.options.HostId,
                    environment.HostId);

            this.poolId = registrationIdentity.PoolId;
            this.hostId = registrationIdentity.HostId;
            this.controlPlaneHostId =
                environment.ControlPlaneHostId;

            this.runtimeInstanceId =
                ResolveRuntimeInstanceId(environment);

            var providerName =
                ResolveProviderName(environment);

            var baseMetadata =
                MergeMetadata(
                    this.options.Metadata,
                    this.options.ProviderMetadata,
                    environment.ProviderMetadata,
                    new Dictionary<string, string>
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = providerName,
                        [AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] = providerName,
                        ["controlPlaneHostId"] = this.controlPlaneHostId ?? string.Empty
                    });

            this.controlPlaneId =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Metadata = baseMetadata,
                            Source = "runtime-instance-registration-hosted-service",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            var controlPlaneMetadata =
                await this.controlPlaneIdResolver
                    .ResolveMetadataAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            RequestedControlPlaneId = this.controlPlaneId,
                            Metadata = baseMetadata,
                            Source = "runtime-instance-registration-hosted-service-metadata",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            this.runtimeMetadata =
                MergeMetadata(
                    baseMetadata,
                    controlPlaneMetadata);

            var tenantId =
                GetMetadataValue(
                    this.runtimeMetadata,
                    AiRuntimeInstanceIsolationMetadataKeys.TenantId);

            var tenantGroupId =
                GetMetadataValue(
                    this.runtimeMetadata,
                    AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId);

            SafeLogInformation(
                "Runtime instance id resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, OptionsRuntimeInstanceId={OptionsRuntimeInstanceId}, EnvironmentRuntimeInstanceId={EnvironmentRuntimeInstanceId}, HostName={HostName}, ProcessId={ProcessId}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, ProviderName={ProviderName}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.options.RuntimeInstanceId,
                environment.RuntimeInstanceId,
                environment.HostName,
                environment.ProcessId,
                this.hostId,
                environment.RuntimeId,
                this.controlPlaneHostId,
                providerName,
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

            SafeLogInformation(
                "Runtime instance registration metadata resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, ProviderName={ProviderName}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, Metadata={Metadata}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId,
                this.options.Role,
                providerName,
                tenantId,
                tenantGroupId,
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                FormatMetadata(this.runtimeMetadata));

            var queueState =
                await this.controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            SafeLogInformation(
                "Runtime instance queue state resolved. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, QueueCapacity={QueueCapacity}, MaxConcurrentRuns={MaxConcurrentRuns}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId,
                this.options.Role,
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
                    RuntimeInstanceId = this.runtimeInstanceId,
                    PoolId = this.poolId,
                    HostId = this.hostId,
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    ControlPlaneId = this.controlPlaneId,
                    HostName = environment.HostName,
                    ProcessId = environment.ProcessId,
                    KubernetesNamespace =
                        GetMetadataValue(
                            this.runtimeMetadata,
                            AiKubernetesRuntimeHostMetadataKeys.Namespace),
                    KubernetesPodName =
                        GetMetadataValue(
                            this.runtimeMetadata,
                            AiKubernetesRuntimeHostMetadataKeys.PodName),
                    KubernetesNodeName =
                        GetMetadataValue(
                            this.runtimeMetadata,
                            AiKubernetesRuntimeHostMetadataKeys.NodeName),
                    RuntimeId = environment.RuntimeId,
                    ControlPlaneHostId = this.controlPlaneHostId,
                    WorkerCount = this.options.WorkerCount,
                    QueueCapacity = this.options.QueueCapacity ?? queueState.QueueCapacity,
                    MaxConcurrentRuns = this.options.MaxConcurrentRuns ?? queueState.MaxConcurrentRuns,
                    RuntimeVersion = this.options.RuntimeVersion,
                    Role = this.options.Role,
                    Metadata = this.runtimeMetadata
                };

            this.runtimeRegistration = registration;

            SafeLogInformation(
                "Runtime instance registration started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId,
                tenantId,
                tenantGroupId,
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

            var snapshot =
                await this.registry
                    .RegisterAsync(registration, cancellationToken)
                    .ConfigureAwait(false);

            var registeredLifecycleEvent =
                await this.AppendRuntimeLifecycleEventOnceAsync(
                        AiRuntimeLifecycleEventType.RuntimeRegistered,
                        snapshot,
                        providerName,
                        causationId: null,
                        previousStatus: null,
                        currentStatus: "registered",
                        cancellationToken)
                    .ConfigureAwait(false);

            var readBackSnapshot =
                await this.registry
                    .GetAsync(
                        this.runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance registration readback completed. RuntimeInstanceId={RuntimeInstanceId}, Found={Found}, ReadBackControlPlaneId={ReadBackControlPlaneId}, ReadBackTenantId={ReadBackTenantId}, ReadBackTenantGroupId={ReadBackTenantGroupId}, ReadBackRole={ReadBackRole}, ReadBackStatus={ReadBackStatus}",
                this.runtimeInstanceId,
                readBackSnapshot is not null,
                readBackSnapshot?.ControlPlaneId,
                readBackSnapshot?.TenantId,
                readBackSnapshot?.TenantGroupId,
                readBackSnapshot?.Role,
                readBackSnapshot?.Status);

            SafeLogInformation(
                "Runtime instance registered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, Status={Status}, Provider={Provider}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, Metadata={Metadata}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                snapshot.RuntimeInstanceId,
                snapshot.ControlPlaneId,
                snapshot.Status,
                providerName,
                snapshot.HostId,
                snapshot.RuntimeId,
                snapshot.ControlPlaneHostId,
                snapshot.TenantId,
                snapshot.TenantGroupId,
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                FormatMetadata(snapshot.Metadata),
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

            SafeLogInformation(
                "Runtime instance capacity publication after registry registration started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId);

            await PublishCapacityDescriptorAsync(
                    this.runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    this.runtimeMetadata,
                    cancellationToken)
                .ConfigureAwait(false);

            await this.AppendRuntimeLifecycleEventOnceAsync(
                    AiRuntimeLifecycleEventType.RuntimeReady,
                    snapshot,
                    providerName,
                    registeredLifecycleEvent.EventId,
                    previousStatus: "registered",
                    currentStatus: AiRuntimeInstanceStatus.Ready.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Appends one stable runtime lifecycle event for the current registration incarnation.
        /// </summary>
        private async Task<AiRuntimeLifecycleEvent> AppendRuntimeLifecycleEventOnceAsync(
            string eventType,
            AiRuntimeInstanceSnapshot snapshot,
            string providerName,
            string? causationId,
            string? previousStatus,
            string? currentStatus,
            CancellationToken cancellationToken)
        {
            var registeredAtUtc = snapshot.RegisteredAtUtc == default
                ? DateTimeOffset.UtcNow
                : snapshot.RegisteredAtUtc;
            var eventId = $"{eventType}:{snapshot.ControlPlaneId}:{snapshot.RuntimeInstanceId}:{registeredAtUtc.UtcDateTime.Ticks}";
            var existing = await this.lifecycleJournal
                .GetByEventIdAsync(eventId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                return existing;
            }

            var hostCreationMode = ResolveHostCreationMode(snapshot);
            var isKubernetes = hostCreationMode is
                AiRuntimeHostCreationMode.Kubernetes or
                AiRuntimeHostCreationMode.KubernetesPool;
            var isSharedInfrastructure = !string.IsNullOrWhiteSpace(snapshot.PoolId);
            var correlationId = GetMetadataValue(
                    snapshot.Metadata,
                    AiRuntimeHostMetadataKeys.LifecycleCorrelationId)
                ?? snapshot.HostId
                ?? $"{snapshot.ControlPlaneId}:{snapshot.RuntimeInstanceId}:{registeredAtUtc.UtcDateTime.Ticks}";

            var timestampUtc = eventType == AiRuntimeLifecycleEventType.RuntimeRegistered
                ? registeredAtUtc
                : DateTimeOffset.UtcNow;

            if (eventType != AiRuntimeLifecycleEventType.RuntimeRegistered &&
                timestampUtc <= registeredAtUtc)
            {
                timestampUtc = registeredAtUtc.AddTicks(1);
            }

            var lifecycleEvent = new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = snapshot.ControlPlaneId ?? this.controlPlaneId ?? string.Empty,
                HostCreationMode = hostCreationMode,
                ProviderName = providerName,
                PoolId = snapshot.PoolId,
                HostId = snapshot.HostId,
                KubernetesPodUid = isKubernetes ? snapshot.HostId : null,
                KubernetesNamespace = snapshot.KubernetesNamespace,
                KubernetesPodName = snapshot.KubernetesPodName,
                KubernetesNodeName = snapshot.KubernetesNodeName,
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                RuntimeId = snapshot.RuntimeId,
                ProcessId = snapshot.ProcessId,
                TenantId = isSharedInfrastructure ? null : snapshot.TenantId,
                TenantGroupId = isSharedInfrastructure ? null : snapshot.TenantGroupId,
                CorrelationId = correlationId,
                CausationId = causationId,
                PreviousStatus = previousStatus,
                CurrentStatus = currentStatus,
                Metadata = CreateRuntimeLifecycleMetadata(snapshot)
            };

            await this.lifecycleJournal
                .AppendAsync(lifecycleEvent, cancellationToken)
                .ConfigureAwait(false);

            return lifecycleEvent;
        }

        /// <summary>
        /// Resolves the host creation mode from propagated host metadata and typed runtime identity.
        /// </summary>
        private static AiRuntimeHostCreationMode? ResolveHostCreationMode(
            AiRuntimeInstanceSnapshot snapshot)
        {
            var configuredMode = GetMetadataValue(
                snapshot.Metadata,
                AiRuntimeHostMetadataKeys.HostCreationMode);

            if (Enum.TryParse<AiRuntimeHostCreationMode>(
                    configuredMode,
                    ignoreCase: true,
                    out var hostCreationMode))
            {
                return hostCreationMode;
            }

            var isKubernetes =
                !string.IsNullOrWhiteSpace(snapshot.KubernetesNamespace) ||
                !string.IsNullOrWhiteSpace(snapshot.KubernetesPodName) ||
                !string.IsNullOrWhiteSpace(snapshot.KubernetesNodeName);

            if (isKubernetes)
            {
                return string.IsNullOrWhiteSpace(snapshot.PoolId)
                    ? AiRuntimeHostCreationMode.Kubernetes
                    : AiRuntimeHostCreationMode.KubernetesPool;
            }

            return snapshot.ProcessId.HasValue
                ? AiRuntimeHostCreationMode.Process
                : null;
        }

        /// <summary>
        /// Creates compact non-authoritative diagnostics for one runtime lifecycle event.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CreateRuntimeLifecycleMetadata(
            AiRuntimeInstanceSnapshot snapshot)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(snapshot.HostName))
            {
                metadata["hostName"] = snapshot.HostName;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.RuntimeVersion))
            {
                metadata["runtimeVersion"] = snapshot.RuntimeVersion;
            }

            return metadata;
        }

        /// <summary>
        /// Publishes a heartbeat for the current runtime instance.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task PublishHeartbeatAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(this.runtimeInstanceId))
            {
                SafeLogWarning(
                    "Runtime instance heartbeat skipped because RuntimeInstanceId is empty.");

                return;
            }

            if (string.IsNullOrWhiteSpace(this.controlPlaneId))
            {
                SafeLogWarning(
                    "Runtime instance heartbeat skipped because ControlPlaneId is empty. RuntimeInstanceId={RuntimeInstanceId}",
                    this.runtimeInstanceId);

                return;
            }

            var queueState =
                await this.controller
                    .GetQueueStateAsync(cancellationToken)
                    .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            SafeLogInformation(
                "Runtime instance heartbeat started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Role={Role}, QueuedRunCount={QueuedRunCount}, RunningRunCount={RunningRunCount}, ActiveRunCount={ActiveRunCount}, QueueStateAvailableRunSlots={QueueStateAvailableRunSlots}, EffectiveAvailableRunSlots={EffectiveAvailableRunSlots}, IsPaused={IsPaused}, QueueStateCanAcceptRun={QueueStateCanAcceptRun}, EffectiveCanAcceptRun={EffectiveCanAcceptRun}, QueueHasCapacity={QueueHasCapacity}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, AllowSharedFallback={AllowSharedFallback}, PreferDedicatedCapacity={PreferDedicatedCapacity}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId,
                this.options.Role,
                queueState.QueuedRunCount,
                queueState.RunningRunCount,
                queueState.ActiveRunCount,
                queueState.AvailableRunSlots,
                effectiveCapacity.AvailableRunSlots,
                queueState.IsPaused,
                queueState.CanAcceptRun,
                effectiveCapacity.CanAcceptRun,
                effectiveCapacity.QueueHasCapacity,
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                GetMetadataValue(this.runtimeMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

            await PublishCapacityDescriptorAsync(
                    this.runtimeInstanceId,
                    AiRuntimeInstanceStatus.Ready,
                    queueState,
                    this.runtimeMetadata,
                    cancellationToken)
                .ConfigureAwait(false);

            var snapshot =
                await this.registry
                    .HeartbeatAsync(
                        this.runtimeInstanceId,
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
                    "Runtime instance heartbeat found a missing registry lease. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    this.runtimeInstanceId,
                    this.controlPlaneId,
                    this.controlPlaneHostId,
                    this.registry.GetType().FullName,
                    this.registry.GetHashCode());

                snapshot =
                    await this.RestoreMissingRegistrationAsync(
                            queueState,
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            if (snapshot is not null)
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
                    snapshot.TenantId,
                    snapshot.TenantGroupId,
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                    GetMetadataValue(snapshot.Metadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                    this.registry.GetType().FullName,
                    this.registry.GetHashCode());
            }
        }

        /// <summary>
        /// Restores a runtime registry lease that expired while the runtime process remained alive.
        /// </summary>
        /// <param name="queueState">The current local queue state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The restored heartbeat snapshot, or <see langword="null" /> when restoration could not be completed.</returns>
        private async Task<AiRuntimeInstanceSnapshot?> RestoreMissingRegistrationAsync(
            AiRuntimePipelineQueueState queueState,
            CancellationToken cancellationToken)
        {
            if (this.runtimeRegistration is null ||
                string.IsNullOrWhiteSpace(this.runtimeInstanceId))
            {
                SafeLogWarning(
                    "Runtime instance registry lease restoration skipped because the original registration is unavailable. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                    this.runtimeInstanceId,
                    this.controlPlaneId,
                    this.controlPlaneHostId);

                return null;
            }

            SafeLogWarning(
                "Runtime instance registry lease restoration started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId);

            await this.registry
                .RegisterAsync(
                    this.runtimeRegistration,
                    cancellationToken)
                .ConfigureAwait(false);

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    AiRuntimeInstanceStatus.Ready,
                    queueState);

            var restoredSnapshot =
                await this.registry
                    .HeartbeatAsync(
                        this.runtimeInstanceId,
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

            if (restoredSnapshot is null)
            {
                SafeLogWarning(
                    "Runtime instance registry lease restoration failed after re-registration. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                    this.runtimeInstanceId,
                    this.controlPlaneId,
                    this.controlPlaneHostId);

                return null;
            }

            SafeLogInformation(
                "Runtime instance registry lease restored. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, Status={Status}, RunningRunCount={RunningRunCount}, QueuedRunCount={QueuedRunCount}",
                restoredSnapshot.RuntimeInstanceId,
                restoredSnapshot.ControlPlaneId,
                restoredSnapshot.ControlPlaneHostId,
                restoredSnapshot.Status,
                restoredSnapshot.RunningRunCount,
                restoredSnapshot.QueuedRunCount);

            return restoredSnapshot;
        }

        /// <summary>
        /// Unregisters the current runtime instance from the runtime instance registry.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task UnregisterRuntimeInstanceAsync(
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(this.runtimeInstanceId))
            {
                SafeLogWarning(
                    "Runtime instance unregister skipped because RuntimeInstanceId is empty.");

                return;
            }

            SafeLogInformation(
                "Runtime instance unregister started. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.runtimeInstanceId,
                this.controlPlaneId,
                this.controlPlaneHostId,
                this.registry.GetType().FullName,
                this.registry.GetHashCode());

            var snapshot =
                await this.registry
                    .UnregisterAsync(this.runtimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

            if (snapshot is not null)
            {
                var providerName =
                    GetMetadataValue(
                        snapshot.Metadata,
                        AiRuntimeInstanceProviderMetadataKeys.ProviderName)
                    ?? "unknown";

                await this.AppendRuntimeLifecycleEventOnceAsync(
                        AiRuntimeLifecycleEventType.RuntimeStopped,
                        snapshot,
                        providerName,
                        causationId: null,
                        previousStatus: null,
                        currentStatus: AiRuntimeInstanceStatus.Stopped.ToString(),
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await RemoveCapacityDescriptorAsync(
                    this.runtimeInstanceId,
                    cancellationToken)
                .ConfigureAwait(false);

            SafeLogInformation(
                "Runtime instance unregistered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, Status={Status}, HostId={HostId}, RuntimeId={RuntimeId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                this.runtimeInstanceId,
                snapshot?.ControlPlaneId,
                snapshot?.Status,
                snapshot?.HostId,
                snapshot?.RuntimeId,
                snapshot?.ControlPlaneHostId,
                this.registry.GetType().FullName,
                this.registry.GetHashCode());
        }

        /// <summary>
        /// Publishes the current runtime capacity descriptor to all configured capacity stores.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="status">The runtime instance status.</param>
        /// <param name="queueState">The runtime queue state.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task PublishCapacityDescriptorAsync(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status,
            AiRuntimePipelineQueueState queueState,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            if (this.capacityStores.Count == 0)
            {
                return;
            }

            var effectiveCapacity =
                CreateEffectiveCapacity(
                    status,
                    queueState);

            var descriptorMetadata =
                await this.CreateCapacityDescriptorMetadataAsync(
                        runtimeInstanceId,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            var hasRequiredTransportEndpoint =
                HasRequiredTransportEndpoint(
                    descriptorMetadata);

            var descriptorAvailableRunSlots =
                hasRequiredTransportEndpoint
                    ? effectiveCapacity.AvailableRunSlots
                    : 0;

            var descriptorCanAcceptRun =
                effectiveCapacity.CanAcceptRun &&
                hasRequiredTransportEndpoint;

            var descriptor =
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    PoolId = this.poolId,
                    HostId = this.hostId,
                    ProviderName =
                        ResolveProviderNameFromMetadata(
                            descriptorMetadata),
                    TenantId =
                        GetMetadataValue(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                    TenantGroupId =
                        GetMetadataValue(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                    IsolationMode =
                        ResolveIsolationMode(
                            descriptorMetadata),
                    AllowSharedFallback =
                        ResolveBooleanMetadata(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys
                                .AllowSharedFallback,
                            defaultValue: true),
                    PreferDedicatedCapacity =
                        ResolveBooleanMetadata(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys
                                .PreferDedicatedCapacity,
                            defaultValue: false),
                    ControlPlaneId = this.controlPlaneId,
                    ControlPlaneHostId = this.controlPlaneHostId,
                    Role = this.options.Role,
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
                    AvailableRunSlots = descriptorAvailableRunSlots,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = descriptorAvailableRunSlots,
                    IsQueuePaused = queueState.IsPaused,
                    CanAcceptRun = descriptorCanAcceptRun,
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
                    : descriptorMetadata.TryGetValue(AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName, out var metadataProviderAlias)
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
                    : descriptorMetadata.TryGetValue(AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName, out var providerAlias)
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
                this.capacityStores.Count);

            foreach (var capacityStore in this.capacityStores)
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
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        capacityStore.GetType().FullName);
                }
            }
        }

        /// <summary>
        /// Removes the runtime capacity descriptor from all configured capacity stores.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task RemoveCapacityDescriptorAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken)
        {
            if (this.capacityStores.Count == 0)
            {
                return;
            }

            foreach (var capacityStore in this.capacityStores)
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
                        this.controlPlaneId,
                        this.controlPlaneHostId,
                        capacityStore.GetType().FullName);
                }
            }
        }

        /// <summary>
        /// Creates capacity descriptor metadata while preserving externally published Kubernetes transport endpoints.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="metadata">The runtime metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The normalized capacity descriptor metadata.</returns>
        private async Task<IReadOnlyDictionary<string, string>> CreateCapacityDescriptorMetadataAsync(
            string runtimeInstanceId,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            var result =
                new Dictionary<string, string>(
                    EnsureProviderMetadata(
                        metadata),
                    StringComparer.OrdinalIgnoreCase);

            var hasCurrentTransportEndpoint =
                TryGetTransportEndpoint(
                    result,
                    out var currentTransportEndpoint);

            if (hasCurrentTransportEndpoint &&
                !IsUnsafeKubernetesLocalhostEndpoint(
                    result,
                    currentTransportEndpoint))
            {
                return result;
            }

            var existingDescriptor =
                await this.TryResolveExistingTransportDescriptorAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var transportEndpointSource =
                "preserved-existing-capacity-descriptor";

            if (existingDescriptor is null &&
                IsKubernetesPoolRuntime(result))
            {
                existingDescriptor =
                    await this.TryResolveSiblingTransportDescriptorAsync(
                            runtimeInstanceId,
                            cancellationToken)
                        .ConfigureAwait(false);

                transportEndpointSource =
                    "preserved-sibling-capacity-descriptor";
            }

            if (existingDescriptor is not null &&
                TryGetTransportEndpoint(
                    existingDescriptor.Metadata,
                    out var existingTransportEndpoint))
            {
                CopyExternallyPublishedKubernetesMetadata(
                    result,
                    existingDescriptor.Metadata);

                if (hasCurrentTransportEndpoint &&
                    !string.Equals(
                        currentTransportEndpoint,
                        existingTransportEndpoint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result[AiRuntimeInstanceCommandTransportMetadataKeys.InternalTransportEndpoint] =
                        currentTransportEndpoint;
                }

                AddTransportEndpointAliases(
                    result,
                    existingTransportEndpoint);

                result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointSource] =
                    transportEndpointSource;
                result[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointScope] =
                    "control-plane";
            }

            return result;
        }

        /// <summary>
        /// Resolves an already published control-plane transport descriptor for the runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The existing descriptor, when it owns a usable external endpoint.</returns>
        private async Task<AiRuntimeInstanceCapacityDescriptor?>
            TryResolveExistingTransportDescriptorAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken)
        {
            foreach (var capacityStore in this.capacityStores)
            {
                try
                {
                    var descriptors =
                        await capacityStore
                            .ListAsync(cancellationToken)
                            .ConfigureAwait(false);

                    var existingDescriptor =
                        descriptors.FirstOrDefault(descriptor =>
                            string.Equals(
                                descriptor.RuntimeInstanceId,
                                runtimeInstanceId,
                                StringComparison.Ordinal));

                    if (existingDescriptor is null)
                    {
                        continue;
                    }

                    if (TryGetTransportEndpoint(
                            existingDescriptor.Metadata,
                            out var transportEndpoint) &&
                        !IsUnsafeKubernetesLocalhostEndpoint(
                            existingDescriptor.Metadata,
                            transportEndpoint))
                    {
                        return existingDescriptor;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SafeLogWarning(
                        "Failed to inspect existing runtime capacity descriptor while preserving transport endpoint. RuntimeInstanceId={RuntimeInstanceId}, StoreType={StoreType}, Reason={Reason}",
                        runtimeInstanceId,
                        capacityStore.GetType().FullName,
                        exception.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves one already projected sibling descriptor from the same Kubernetes Runtime
        /// Pool Pod. Dynamic in-Pod replacement processes have a fresh RuntimeInstanceId, so no
        /// descriptor exists under their new identity yet. Any surviving sibling Gateway route
        /// reaches the same stable Pod service; the command body still carries the exact target
        /// RuntimeInstanceId and the in-Pod router performs the final child selection.
        /// </summary>
        private async Task<AiRuntimeInstanceCapacityDescriptor?>
            TryResolveSiblingTransportDescriptorAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(this.poolId) ||
                string.IsNullOrWhiteSpace(this.hostId))
            {
                return null;
            }

            foreach (var capacityStore in this.capacityStores)
            {
                try
                {
                    var descriptors =
                        await capacityStore
                            .ListAsync(cancellationToken)
                            .ConfigureAwait(false);

                    var siblingDescriptor =
                        descriptors
                            .Where(descriptor =>
                                !string.Equals(
                                    descriptor.RuntimeInstanceId,
                                    runtimeInstanceId,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    descriptor.PoolId,
                                    this.poolId,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    descriptor.HostId,
                                    this.hostId,
                                    StringComparison.Ordinal) &&
                                TryGetTransportEndpoint(
                                    descriptor.Metadata,
                                    out var siblingTransportEndpoint) &&
                                !IsUnsafeKubernetesLocalhostEndpoint(
                                    descriptor.Metadata,
                                    siblingTransportEndpoint) &&
                                !string.IsNullOrWhiteSpace(
                                    GetMetadataValue(
                                        descriptor.Metadata,
                                        AiRuntimeInstanceCommandTransportMetadataKeys.GatewayRoutingHeader)) &&
                                !string.IsNullOrWhiteSpace(
                                    GetMetadataValue(
                                        descriptor.Metadata,
                                        AiRuntimeInstanceCommandTransportMetadataKeys.GatewayRoutingValue)))
                            .OrderBy(
                                descriptor => descriptor.RuntimeInstanceId,
                                StringComparer.Ordinal)
                            .FirstOrDefault();

                    if (siblingDescriptor is not null)
                    {
                        SafeLogInformation(
                            "Runtime instance preserved control-plane transport from Kubernetes Pool sibling. RuntimeInstanceId={RuntimeInstanceId}, SiblingRuntimeInstanceId={SiblingRuntimeInstanceId}, PoolId={PoolId}, HostId={HostId}",
                            runtimeInstanceId,
                            siblingDescriptor.RuntimeInstanceId,
                            this.poolId,
                            this.hostId);

                        return siblingDescriptor;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    SafeLogWarning(
                        "Failed to inspect sibling runtime capacity descriptors while preserving Kubernetes Pool transport. RuntimeInstanceId={RuntimeInstanceId}, PoolId={PoolId}, HostId={HostId}, StoreType={StoreType}, Reason={Reason}",
                        runtimeInstanceId,
                        this.poolId,
                        this.hostId,
                        capacityStore.GetType().FullName,
                        exception.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Preserves externally projected Kubernetes and Gateway routing metadata while a child
        /// heartbeat refreshes its operational capacity values.
        /// </summary>
        private static void CopyExternallyPublishedKubernetesMetadata(
            IDictionary<string, string> destination,
            IReadOnlyDictionary<string, string> source)
        {
            foreach (var pair in source)
            {
                if (pair.Key.StartsWith(
                        "kubernetes.",
                        StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith(
                        "runtime.pool.",
                        StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith(
                        "gateway.routing.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    destination[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>
        /// Determines whether the descriptor has the required transport endpoint before it can accept runs.
        /// </summary>
        /// <param name="metadata">The descriptor metadata.</param>
        /// <returns><see langword="true"/> when the descriptor can be admitted for dispatch.</returns>
        private static bool HasRequiredTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata)
        {
            if (!IsKubernetesRemoteRuntime(metadata))
            {
                return true;
            }

            return TryGetTransportEndpoint(
                       metadata,
                       out var transportEndpoint) &&
                   !string.IsNullOrWhiteSpace(transportEndpoint) &&
                   !IsUnsafeKubernetesLocalhostEndpoint(
                       metadata,
                       transportEndpoint);
        }

        /// <summary>
        /// Determines whether the descriptor represents a Kubernetes-backed remote runtime.
        /// </summary>
        /// <param name="metadata">The descriptor metadata.</param>
        /// <returns>
        /// <see langword="true"/> when this is a Kubernetes HTTP or gRPC runtime descriptor.
        /// </returns>
        private static bool IsKubernetesRemoteRuntime(
            IReadOnlyDictionary<string, string> metadata)
        {
            var provider =
                GetMetadataValue(metadata, AiRuntimeInstanceProviderMetadataKeys.ProviderName) ??
                GetMetadataValue(metadata, AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName);

            var transport =
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportName) ??
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportName) ??
                provider;

            var isRemoteTransport =
                string.Equals(provider, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "grpc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    transport,
                    AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    transport,
                    AiRuntimeInstanceCommandTransportMetadataKeys.GrpcTransportName,
                    StringComparison.OrdinalIgnoreCase);

            var hostProvider =
                GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.HostProvider);

            var hostType =
                GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.CamelCaseHostType);

            var deployment =
                GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.Deployment);

            var isKubernetes =
                string.Equals(hostProvider, "kubernetes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hostType, AiRuntimeHostTypeNames.Kubernetes, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deployment, AiRuntimeHostDeploymentNames.KubernetesHost, StringComparison.OrdinalIgnoreCase);

            return isRemoteTransport &&
                   isKubernetes;
        }

        /// <summary>
        /// Gets a transport endpoint from metadata aliases.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <param name="transportEndpoint">The resolved transport endpoint.</param>
        /// <returns><see langword="true"/> when an endpoint exists.</returns>
        private static bool TryGetTransportEndpoint(
            IReadOnlyDictionary<string, string>? metadata,
            out string transportEndpoint)
        {
            transportEndpoint =
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint) ??
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint) ??
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeCommandEndpoint) ??
                GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.GrpcEndpoint) ??
                string.Empty;

            return !string.IsNullOrWhiteSpace(transportEndpoint);
        }

        /// <summary>
        /// Adds all known transport endpoint metadata aliases.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        private static void AddTransportEndpointAliases(
            IDictionary<string, string> metadata,
            string transportEndpoint)
        {
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = transportEndpoint;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = transportEndpoint;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeCommandEndpoint] = transportEndpoint;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.GrpcEndpoint] = transportEndpoint;
        }

        /// <summary>
        /// Determines whether a Kubernetes endpoint is unsafe for multi-pod dispatch.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns><see langword="true"/> when the endpoint is unsafe.</returns>
        private static bool IsUnsafeKubernetesLocalhostEndpoint(
            IReadOnlyDictionary<string, string> metadata,
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return false;
            }

            if (IsControlPlaneTransportEndpoint(metadata))
            {
                return false;
            }

            if (IsKubernetesPoolRuntime(metadata) &&
                IsLoopbackTransportEndpoint(transportEndpoint))
            {
                return true;
            }

            return transportEndpoint.Contains(
                       "127.0.0.1:8080",
                       StringComparison.OrdinalIgnoreCase) ||
                   transportEndpoint.Contains(
                       "localhost:8080",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether metadata identifies a Kubernetes Runtime Pool child.
        /// </summary>
        private static bool IsKubernetesPoolRuntime(
            IReadOnlyDictionary<string, string> metadata)
        {
            return string.Equals(
                       GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.HostCreationMode),
                       AiRuntimeHostCreationModeNames.KubernetesPool,
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.CamelCaseHostType),
                       AiRuntimeHostTypeNames.KubernetesPool,
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       GetMetadataValue(metadata, AiRuntimeHostMetadataKeys.Deployment),
                       AiRuntimeHostDeploymentNames.KubernetesPool,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether the endpoint was explicitly projected for control-plane routing.
        /// </summary>
        private static bool IsControlPlaneTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata)
        {
            return string.Equals(
                       GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointScope),
                       "control-plane",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointSource),
                       "kubernetes-pool-service",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointSource),
                       "preserved-existing-capacity-descriptor",
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       GetMetadataValue(metadata, AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpointSource),
                       "preserved-existing-capacity-descriptor-compare-exchange",
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether an absolute transport endpoint resolves to the local machine.
        /// </summary>
        private static bool IsLoopbackTransportEndpoint(
            string transportEndpoint)
        {
            return Uri.TryCreate(
                       transportEndpoint,
                       UriKind.Absolute,
                       out var uri)
                ? uri.IsLoopback
                : transportEndpoint.Contains(
                      "localhost",
                      StringComparison.OrdinalIgnoreCase) ||
                  transportEndpoint.Contains(
                      "127.0.0.1",
                      StringComparison.OrdinalIgnoreCase) ||
                  transportEndpoint.Contains(
                      "[::1]",
                      StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates the effective capacity values that must be published to the registry and capacity stores.
        /// </summary>
        /// <param name="status">The runtime instance status.</param>
        /// <param name="queueState">The local runtime queue state.</param>
        /// <returns>The effective runtime capacity.</returns>
        private EffectiveRuntimeCapacity CreateEffectiveCapacity(
            AiRuntimeInstanceStatus status,
            AiRuntimePipelineQueueState queueState)
        {
            var role =
                this.options.Role;

            var workerCount =
                queueState.WorkerCount ?? this.options.WorkerCount;

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

            var hasImmediateRunCapacity =
                queueState.AvailableRunSlots.GetValueOrDefault() > 0;

            var canAcceptRun =
                status == AiRuntimeInstanceStatus.Ready &&
                !queueState.IsPaused &&
                (hasImmediateRunCapacity ||
                 queueHasCapacity);

            return new EffectiveRuntimeCapacity(
                WorkerCount: workerCount,
                ActiveWorkerCount: activeWorkerCount,
                AvailableWorkerCount: availableWorkerCount,
                AvailableRunSlots: queueState.AvailableRunSlots,
                MaxLocalWorkersPerExecution: queueState.MaxLocalWorkersPerExecution,
                QueueHasCapacity: queueHasCapacity,
                CanAcceptRun: canAcceptRun);
        }

        /// <summary>
        /// Resolves the runtime instance identifier from options or environment.
        /// </summary>
        /// <param name="environment">The runtime environment snapshot.</param>
        /// <returns>The resolved runtime instance identifier.</returns>
        private string ResolveRuntimeInstanceId(
            AiRuntimeEnvironmentSnapshot environment)
        {
            if (!string.IsNullOrWhiteSpace(this.options.RuntimeInstanceId))
            {
                return this.options.RuntimeInstanceId;
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
                this.options.ProviderName ?? environment.ProviderName;

            if (string.IsNullOrWhiteSpace(providerName))
            {
                return AiRuntimeInstanceProviderNames.Local;
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

            result[AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName] =
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
                    AiRuntimeInstanceProviderMetadataKeys.LegacyProviderName,
                    out var providerAlias) &&
                !string.IsNullOrWhiteSpace(providerAlias))
            {
                return providerAlias.Trim();
            }

            var configuredProviderName =
                this.options.ProviderName;

            if (!string.IsNullOrWhiteSpace(configuredProviderName))
            {
                return configuredProviderName.Trim();
            }

            return AiRuntimeInstanceProviderNames.Local;
        }

        /// <summary>
        /// Merges metadata dictionaries.
        /// </summary>
        /// <param name="sources">The metadata sources.</param>
        /// <returns>The merged metadata dictionary.</returns>
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

        /// <summary>
        /// Resolves the first-class runtime isolation mode from normalized runtime
        /// metadata at the publication boundary.
        /// </summary>
        /// <param name="metadata">The normalized runtime metadata.</param>
        /// <returns>
        /// The parsed isolation mode, or
        /// <see cref="AiRuntimeInstanceIsolationMode.Shared" /> when no valid value
        /// was published.
        /// </returns>
        private static AiRuntimeInstanceIsolationMode ResolveIsolationMode(
            IReadOnlyDictionary<string, string> metadata)
        {
            var value =
                GetMetadataValue(
                    metadata,
                    AiRuntimeInstanceIsolationMetadataKeys.IsolationMode);

            return Enum.TryParse<AiRuntimeInstanceIsolationMode>(
                    value,
                    ignoreCase: true,
                    out var parsed)
                ? parsed
                : AiRuntimeInstanceIsolationMode.Shared;
        }

        /// <summary>
        /// Resolves one first-class Boolean capacity field from normalized runtime
        /// metadata at the publication boundary.
        /// </summary>
        /// <param name="metadata">The normalized runtime metadata.</param>
        /// <param name="key">The canonical metadata key.</param>
        /// <param name="defaultValue">The value used when no valid value was published.</param>
        /// <returns>The parsed Boolean value.</returns>
        private static bool ResolveBooleanMetadata(
            IReadOnlyDictionary<string, string> metadata,
            string key,
            bool defaultValue)
        {
            var value =
                GetMetadataValue(
                    metadata,
                    key);

            return bool.TryParse(
                    value,
                    out var parsed)
                ? parsed
                : defaultValue;
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
                this.logger.LogInformation(
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
                this.logger.LogWarning(
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
                this.logger.LogError(
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
