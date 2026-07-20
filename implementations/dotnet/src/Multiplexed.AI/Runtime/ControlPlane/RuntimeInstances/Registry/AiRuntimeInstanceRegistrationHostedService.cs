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

        private string? runtimeInstanceId;
        private string? controlPlaneId;
        private string? controlPlaneHostId;
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
                        ["provider"] = providerName,
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
                environment.HostId,
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
                    TenantId = tenantId,
                    TenantGroupId = tenantGroupId,
                    ControlPlaneId = this.controlPlaneId,
                    HostName = environment.HostName,
                    ProcessId = environment.ProcessId,
                    HostId = environment.HostId,
                    RuntimeId = environment.RuntimeId,
                    ControlPlaneHostId = this.controlPlaneHostId,
                    WorkerCount = this.options.WorkerCount,
                    QueueCapacity = this.options.QueueCapacity ?? queueState.QueueCapacity,
                    MaxConcurrentRuns = this.options.MaxConcurrentRuns ?? queueState.MaxConcurrentRuns,
                    RuntimeVersion = this.options.RuntimeVersion,
                    Role = this.options.Role,
                    Metadata = this.runtimeMetadata
                };

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
                    "Runtime instance heartbeat ignored because instance is not registered. RuntimeInstanceId={RuntimeInstanceId}, ControlPlaneId={ControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RegistryType={RegistryType}, RegistryHash={RegistryHash}",
                    this.runtimeInstanceId,
                    this.controlPlaneId,
                    this.controlPlaneHostId,
                    this.registry.GetType().FullName,
                    this.registry.GetHashCode());
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
                    TenantId =
                        GetMetadataValue(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                    TenantGroupId =
                        GetMetadataValue(
                            descriptorMetadata,
                            AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
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

            if (HasTransportEndpoint(result))
            {
                return result;
            }

            var existingTransportEndpoint =
                await this.TryResolveExistingTransportEndpointAsync(
                        runtimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(existingTransportEndpoint))
            {
                AddTransportEndpointAliases(
                    result,
                    existingTransportEndpoint);

                result["transport.endpoint.source"] = "preserved-existing-capacity-descriptor";
            }

            return result;
        }

        /// <summary>
        /// Resolves an already published transport endpoint for the runtime instance.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The existing transport endpoint, when available.</returns>
        private async Task<string?> TryResolveExistingTransportEndpointAsync(
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
                        !IsUnsafeKubernetesLocalhostEndpoint(transportEndpoint))
                    {
                        return transportEndpoint;
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
                   !IsUnsafeKubernetesLocalhostEndpoint(transportEndpoint);
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
                GetMetadataValue(metadata, "provider");

            var transport =
                GetMetadataValue(metadata, "transport.name") ??
                GetMetadataValue(metadata, "transportName") ??
                provider;

            var isRemoteTransport =
                string.Equals(provider, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(provider, "grpc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transport, "grpc", StringComparison.OrdinalIgnoreCase);

            var hostProvider =
                GetMetadataValue(metadata, "host.provider");

            var hostType =
                GetMetadataValue(metadata, "hostType");

            var deployment =
                GetMetadataValue(metadata, "deployment");

            var isKubernetes =
                string.Equals(hostProvider, "kubernetes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hostType, "runtime-instance-kubernetes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(deployment, "kubernetes-host", StringComparison.OrdinalIgnoreCase);

            return isRemoteTransport &&
                   isKubernetes;
        }

        /// <summary>
        /// Determines whether metadata already contains a transport endpoint.
        /// </summary>
        /// <param name="metadata">The metadata.</param>
        /// <returns><see langword="true"/> when a transport endpoint exists.</returns>
        private static bool HasTransportEndpoint(
            IReadOnlyDictionary<string, string> metadata)
        {
            return TryGetTransportEndpoint(
                metadata,
                out _);
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
                GetMetadataValue(metadata, "transport.endpoint") ??
                GetMetadataValue(metadata, "transportEndpoint") ??
                GetMetadataValue(metadata, "runtime.command.endpoint") ??
                GetMetadataValue(metadata, "grpc.endpoint") ??
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
            metadata["transport.endpoint"] = transportEndpoint;
            metadata["transportEndpoint"] = transportEndpoint;
            metadata["runtime.command.endpoint"] = transportEndpoint;
            metadata["grpc.endpoint"] = transportEndpoint;
        }

        /// <summary>
        /// Determines whether a Kubernetes endpoint is unsafe for multi-pod dispatch.
        /// </summary>
        /// <param name="transportEndpoint">The transport endpoint.</param>
        /// <returns><see langword="true"/> when the endpoint is unsafe.</returns>
        private static bool IsUnsafeKubernetesLocalhostEndpoint(
            string? transportEndpoint)
        {
            if (string.IsNullOrWhiteSpace(transportEndpoint))
            {
                return false;
            }

            return transportEndpoint.Contains(
                       "127.0.0.1:8080",
                       StringComparison.OrdinalIgnoreCase) ||
                   transportEndpoint.Contains(
                       "localhost:8080",
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

            var canAcceptRun =
                status == AiRuntimeInstanceStatus.Ready &&
                !queueState.IsPaused &&
                queueHasCapacity;

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
                this.options.ProviderName;

            if (!string.IsNullOrWhiteSpace(configuredProviderName))
            {
                return configuredProviderName.Trim();
            }

            return "local";
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
