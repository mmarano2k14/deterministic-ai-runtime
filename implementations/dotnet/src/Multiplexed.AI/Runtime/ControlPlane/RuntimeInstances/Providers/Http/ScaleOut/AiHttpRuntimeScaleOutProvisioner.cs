using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut
{
    /// <summary>
    /// Provisions HTTP runtime capacity for HTTP provider scale-out requests.
    /// </summary>
    /// <remarks>
    /// This provisioner primarily consumes tenant-aware runtime settings carried by
    /// <see cref="AiRuntimeScaleOutProviderRequest" />.
    ///
    /// When older request paths do not carry tenant-aware fields such as runtime
    /// instance prefix, worker count, queue capacity, or isolation flags, this
    /// provisioner falls back to <see cref="IAiTenantRuntimeSettingsProvider" />.
    ///
    /// HTTP scale-out options remain provider technical defaults only.
    /// </remarks>
    public sealed class AiHttpRuntimeScaleOutProvisioner : IAiHttpRuntimeScaleOutProvisioner
    {
        /// <summary>
        /// HTTP provider name.
        /// </summary>
        private const string ProviderName = "http";

        /// <summary>
        /// Default HTTP runtime instance id prefix used only as a technical fallback.
        /// </summary>
        private const string DefaultRuntimeInstanceIdPrefix = "http-runtime";

        /// <summary>
        /// Default HTTP runtime endpoint used only as a technical fallback.
        /// </summary>
        private const string DefaultEndpointTemplate = "http://localhost";

        /// <summary>
        /// Default worker count used only when the tenant-aware request does not provide one.
        /// </summary>
        private const int DefaultWorkerCountPerInstance = 1;

        /// <summary>
        /// Default maximum concurrent run count used only when the tenant-aware request does not provide one.
        /// </summary>
        private const int DefaultMaxConcurrentRunsPerInstance = 1;

        /// <summary>
        /// Default local queue capacity used only when the tenant-aware request does not provide one.
        /// </summary>
        private const int DefaultQueueCapacity = 100;

        private readonly IAiRuntimeInstanceRegistry registry;
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeHostManager runtimeHostManager;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider;
        private readonly AiHttpRuntimeScaleOutOptions options;
        private readonly ILogger<AiHttpRuntimeScaleOutProvisioner> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiHttpRuntimeScaleOutProvisioner"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeHostManager">The runtime host manager.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        /// <param name="options">The HTTP scale-out technical options.</param>
        /// <param name="logger">The logger.</param>
        public AiHttpRuntimeScaleOutProvisioner(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeHostManager runtimeHostManager,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiHttpRuntimeScaleOutOptions> options,
            ILogger<AiHttpRuntimeScaleOutProvisioner> logger)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            this.runtimeHostManager = runtimeHostManager ?? throw new ArgumentNullException(nameof(runtimeHostManager));
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
            this.tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));

            ArgumentNullException.ThrowIfNull(options);

            this.options = options.Value ?? throw new ArgumentException("HTTP runtime scale-out options must be provided.", nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            if (!this.options.Enabled)
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-disabled", "HTTP runtime scale-out is disabled.");
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-request-id-missing", "HTTP runtime scale-out request id is missing.");
            }

            if (string.IsNullOrWhiteSpace(request.ControlPlaneId))
            {
                return CreateRejectedResult(request, "http-runtime-scaleout-control-plane-id-missing", "HTTP runtime scale-out control-plane id is missing.");
            }

            var context = CreateProvisioningContext(request);

            if (IsHostManagerMode(this.options.Mode))
            {
                return await this.ProvisionWithHostManagerAsync(request, context, startedAtUtc, cancellationToken).ConfigureAwait(false);
            }

            this.logger.LogInformation(
                "HTTP SCALE-OUT PROVISION START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint,
                context.TenantId,
                context.TenantGroupId,
                context.IsolationMode,
                context.WorkerCount,
                context.MaxConcurrentRuns,
                context.QueueCapacity);

            await this.registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = context.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                    HostId = $"http-host-{context.RuntimeInstanceId}",
                    RuntimeId = context.RuntimeInstanceId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = context.WorkerCount,
                    MaxConcurrentRuns = context.MaxConcurrentRuns,
                    QueueCapacity = context.QueueCapacity,
                    RegisteredAtUtc = startedAtUtc,
                    Metadata = context.Metadata
                },
                cancellationToken).ConfigureAwait(false);

            await this.capacityStore.PublishAsync(
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = context.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                    Role = AiRuntimeInstanceRole.Runtime,
                    Status = AiRuntimeInstanceStatus.Ready,
                    WorkerCount = context.WorkerCount,
                    ActiveWorkerCount = 0,
                    AvailableWorkerCount = context.WorkerCount,
                    MaxWorkersPerRun = context.WorkerCount,
                    MinWorkersRequiredPerRun = 1,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    MaxConcurrentRuns = context.MaxConcurrentRuns,
                    MaxRunSlots = context.MaxConcurrentRuns,
                    AvailableRunSlots = context.MaxConcurrentRuns,
                    ReservedRunSlots = 0,
                    EffectiveAvailableRunSlots = context.MaxConcurrentRuns,
                    IsQueuePaused = false,
                    CanAcceptRun = true,
                    LastHeartbeatAtUtc = startedAtUtc,
                    Metadata = context.Metadata
                },
                cancellationToken).ConfigureAwait(false);

            this.logger.LogInformation(
                "HTTP SCALE-OUT PROVISION FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint);

            return CreateFulfilledResult(
                request,
                context.RuntimeInstanceId,
                $"http-scaleout-{request.RequestId}",
                "HTTP runtime scale-out request was fulfilled.",
                context.Metadata);
        }

        /// <summary>
        /// Provisions HTTP runtime capacity by delegating runtime lifecycle to the runtime host manager.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="context">The resolved provisioning context.</param>
        /// <param name="startedAtUtc">The UTC timestamp when provisioning started.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        private async Task<AiRuntimeScaleOutProviderResult> ProvisionWithHostManagerAsync(
            AiRuntimeScaleOutProviderRequest request,
            ProvisioningContext context,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint,
                this.options.HostCreationMode,
                context.TenantId,
                context.TenantGroupId,
                context.IsolationMode,
                context.WorkerCount,
                context.MaxConcurrentRuns,
                context.QueueCapacity);

            var startResult =
                await this.runtimeHostManager.StartRuntimeAsync(
                    new AiRuntimeHostStartRequest
                    {
                        RequestId = request.RequestId,
                        ControlPlaneId = request.ControlPlaneId,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = context.RuntimeInstanceId,
                        RuntimeInstanceIdPrefix = context.RuntimeInstanceIdPrefix,
                        ProviderName = ProviderName,
                        TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                        TransportEndpoint = context.Endpoint,
                        HostCreationMode = this.options.HostCreationMode,
                        TenantId = context.TenantId,
                        TenantGroupId = context.TenantGroupId,
                        IsolationMode = context.IsolationMode.ToString(),
                        PreferDedicatedCapacity = context.PreferDedicatedCapacity,
                        AllowSharedFallback = context.AllowSharedFallback,
                        WorkerCountPerInstance = context.WorkerCount,
                        MaxConcurrentRunsPerInstance = context.MaxConcurrentRuns,
                        LocalQueueCapacity = context.QueueCapacity,
                        MaxRuntimeInstances = context.MaxRuntimeInstances,
                        Metadata = context.Metadata
                    },
                    cancellationToken).ConfigureAwait(false);

            if (!startResult.Success)
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    this.options.HostCreationMode,
                    startResult.FailureReason);

                return CreateRejectedResult(
                    request,
                    startResult.FailureReason ?? "runtime-host-start-failed",
                    "HTTP runtime scale-out host manager start failed.");
            }

            var fulfilledRuntimeInstanceId =
                !string.IsNullOrWhiteSpace(startResult.RuntimeInstanceId)
                    ? startResult.RuntimeInstanceId
                    : context.RuntimeInstanceId;

            var fulfilledTransportEndpoint =
                !string.IsNullOrWhiteSpace(startResult.TransportEndpoint)
                    ? startResult.TransportEndpoint
                    : context.Endpoint;

            if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    this.options.HostCreationMode,
                    "runtime-host-started-without-runtime-instance-id");

                return CreateRejectedResult(
                    request,
                    "runtime-host-started-without-runtime-instance-id",
                    "HTTP runtime scale-out host manager returned success without a runtime instance id.");
            }

            if (this.options.RequireReadiness)
            {
                var readinessResult =
                    await this.readinessWaiter.WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = fulfilledRuntimeInstanceId,
                            ProviderName = ProviderName,
                            TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                            TransportEndpoint = fulfilledTransportEndpoint,
                            RequireTransportEndpoint = this.options.HostCreationMode != AiRuntimeHostCreationMode.Fixture,
                            Timeout = TimeSpan.FromSeconds(Math.Max(1, this.options.ReadinessTimeoutSeconds)),
                            PollInterval = TimeSpan.FromMilliseconds(Math.Max(1, this.options.ReadinessPollIntervalMilliseconds))
                        },
                        cancellationToken).ConfigureAwait(false);

                if (!readinessResult.Success)
                {
                    this.logger.LogWarning(
                        "HTTP SCALE-OUT HOST-MANAGER READINESS FAILED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason} TimedOut={TimedOut}",
                        request.RequestId,
                        request.SharedRunId,
                        fulfilledRuntimeInstanceId,
                        this.options.HostCreationMode,
                        readinessResult.FailureReason,
                        readinessResult.TimedOut);

                    return CreateRejectedResult(
                        request,
                        readinessResult.FailureReason ?? "runtime-readiness-failed",
                        "HTTP runtime scale-out readiness check failed.");
                }
            }

            var metadata =
                CreateFulfilledHostManagerMetadata(
                    request,
                    context,
                    startResult,
                    fulfilledRuntimeInstanceId,
                    fulfilledTransportEndpoint);

            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} DurationMs={DurationMs}",
                request.RequestId,
                request.SharedRunId,
                fulfilledRuntimeInstanceId,
                fulfilledTransportEndpoint,
                this.options.HostCreationMode,
                (DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds);

            return CreateFulfilledResult(
                request,
                fulfilledRuntimeInstanceId,
                $"http-host-manager-scaleout-{request.RequestId}",
                "HTTP runtime scale-out request was fulfilled by the runtime host manager.",
                metadata);
        }

        /// <summary>
        /// Creates the tenant-aware provisioning context for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <returns>The resolved provisioning context.</returns>
        private ProvisioningContext CreateProvisioningContext(
            AiRuntimeScaleOutProviderRequest request)
        {
            var tenantSettings =
                this.tenantRuntimeSettingsProvider.GetSettings(
                    request.TenantId,
                    request.TenantGroupId);

            var tenantId =
                ResolveText(
                    request.TenantId,
                    tenantSettings.TenantId,
                    "shared");

            var tenantGroupId =
                ResolveText(
                    request.TenantGroupId,
                    tenantSettings.TenantGroupId,
                    string.Empty);

            var isolationMode =
                ResolveIsolationMode(
                    request,
                    tenantSettings);

            var preferDedicatedCapacity =
                ResolveBoolean(
                    request.PreferDedicatedCapacity,
                    tenantSettings.PreferDedicatedCapacity);

            var allowSharedFallback =
                ResolveBoolean(
                    request.AllowSharedFallback,
                    tenantSettings.AllowSharedFallback);

            var runtimeInstanceIdPrefix =
                ResolveRuntimeInstanceIdPrefix(
                    request,
                    tenantSettings);

            this.logger.LogInformation(
                "HTTP SCALE-OUT TENANT SETTINGS RESOLVED RequestId={RequestId} TenantId={TenantId} RequestedPrefix={RequestedPrefix} TenantSettingsPrefix={TenantSettingsPrefix} ResolvedPrefix={ResolvedPrefix} IsolationMode={IsolationMode} PreferDedicatedCapacity={PreferDedicatedCapacity} AllowSharedFallback={AllowSharedFallback}",
                request.RequestId,
                request.TenantId,
                request.RuntimeInstanceIdPrefix,
                tenantSettings.RuntimeInstanceIdPrefix,
                runtimeInstanceIdPrefix,
                tenantSettings.IsolationMode,
                tenantSettings.PreferDedicatedCapacity,
                tenantSettings.AllowSharedFallback);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    request,
                    runtimeInstanceIdPrefix);

            var endpoint =
                ResolveEndpoint(
                    request,
                    runtimeInstanceId,
                    runtimeInstanceIdPrefix);

            var workerCount =
                ResolvePositiveOrDefault(
                    request.WorkerCountPerInstance,
                    tenantSettings.WorkerCountPerInstance,
                    DefaultWorkerCountPerInstance);

            var maxConcurrentRuns =
                ResolvePositiveOrDefault(
                    request.MaxConcurrentRunsPerInstance,
                    tenantSettings.MaxConcurrentRunsPerInstance,
                    DefaultMaxConcurrentRunsPerInstance);

            var queueCapacity =
                ResolvePositiveOrDefault(
                    request.LocalQueueCapacity,
                    tenantSettings.LocalQueueCapacity,
                    DefaultQueueCapacity);

            var maxRuntimeInstances =
                request.MaxRuntimeInstances ??
                tenantSettings.MaxRuntimeInstances;

            var metadata =
                CreateMetadata(
                    request,
                    tenantSettings,
                    tenantId,
                    tenantGroupId,
                    isolationMode,
                    preferDedicatedCapacity,
                    allowSharedFallback,
                    runtimeInstanceId,
                    runtimeInstanceIdPrefix,
                    endpoint,
                    workerCount,
                    maxConcurrentRuns,
                    queueCapacity,
                    maxRuntimeInstances);

            return new ProvisioningContext
            {
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                IsolationMode = isolationMode,
                PreferDedicatedCapacity = preferDedicatedCapacity,
                AllowSharedFallback = allowSharedFallback,
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeInstanceIdPrefix = runtimeInstanceIdPrefix,
                Endpoint = endpoint,
                WorkerCount = workerCount,
                MaxConcurrentRuns = maxConcurrentRuns,
                QueueCapacity = queueCapacity,
                MaxRuntimeInstances = maxRuntimeInstances,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Resolves the tenant-aware runtime instance prefix from tenant settings, request, or HTTP technical options.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns>The runtime instance id prefix.</returns>
        /// <remarks>
        /// Tenant runtime settings are the source of truth because they represent the resolved
        /// tenant isolation policy. The request value is only a carried copy and may still
        /// contain legacy technical defaults such as <c>runtime-instance</c>.
        /// </remarks>
        private string ResolveRuntimeInstanceIdPrefix(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings)
        {
            if (!string.IsNullOrWhiteSpace(tenantSettings.RuntimeInstanceIdPrefix))
            {
                return tenantSettings.RuntimeInstanceIdPrefix.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix))
            {
                return request.RuntimeInstanceIdPrefix.Trim();
            }

            if (!string.IsNullOrWhiteSpace(this.options.DefaultRuntimeInstanceIdPrefix))
            {
                return this.options.DefaultRuntimeInstanceIdPrefix.Trim();
            }

            return DefaultRuntimeInstanceIdPrefix;
        }

        /// <summary>
        /// Resolves the runtime instance id for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceIdPrefix">The resolved runtime instance id prefix.</param>
        /// <returns>The runtime instance id.</returns>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceIdPrefix)
        {
            var target =
                request.RequestedTargetInstanceCount > 0
                    ? request.RequestedTargetInstanceCount
                    : Math.Max(1, request.CurrentInstanceCount + 1);

            return $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-{target}";
        }

        /// <summary>
        /// Resolves the HTTP endpoint for the newly materialized runtime instance.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The resolved runtime instance id prefix.</param>
        /// <returns>The HTTP endpoint.</returns>
        private string ResolveEndpoint(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix)
        {
            var endpointTemplate =
                string.IsNullOrWhiteSpace(this.options.EndpointTemplate)
                    ? DefaultEndpointTemplate
                    : this.options.EndpointTemplate.Trim();

            return endpointTemplate
                .Replace("{runtimeInstanceId}", runtimeInstanceId, StringComparison.OrdinalIgnoreCase)
                .Replace("{tenantId}", request.TenantId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{tenantGroupId}", request.TenantGroupId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{controlPlaneId}", request.ControlPlaneId, StringComparison.OrdinalIgnoreCase)
                .Replace("{runtimeInstanceIdPrefix}", runtimeInstanceIdPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the isolation mode from the request or tenant settings.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns>The resolved isolation mode.</returns>
        private static AiRuntimeInstanceIsolationMode ResolveIsolationMode(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings)
        {
            return request.IsolationMode == default
                ? tenantSettings.IsolationMode
                : request.IsolationMode;
        }

        /// <summary>
        /// Resolves a boolean value using logical OR compatibility semantics.
        /// </summary>
        /// <param name="requestValue">The request value.</param>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <returns>The resolved boolean value.</returns>
        private static bool ResolveBoolean(
            bool requestValue,
            bool tenantValue)
        {
            return requestValue || tenantValue;
        }

        /// <summary>
        /// Resolves the first non-empty text value.
        /// </summary>
        /// <param name="first">The first candidate.</param>
        /// <param name="second">The second candidate.</param>
        /// <param name="fallback">The fallback value.</param>
        /// <returns>The resolved text.</returns>
        private static string ResolveText(
            string? first,
            string? second,
            string fallback)
        {
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first.Trim();
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                return second.Trim();
            }

            return fallback;
        }

        /// <summary>
        /// Resolves a positive integer value from request, tenant settings, or hard default.
        /// </summary>
        /// <param name="requestValue">The request value.</param>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <param name="hardDefault">The hard fallback value.</param>
        /// <returns>The resolved positive value.</returns>
        private static int ResolvePositiveOrDefault(
            int? requestValue,
            int? tenantValue,
            int hardDefault)
        {
            if (requestValue.HasValue && requestValue.Value > 0)
            {
                return requestValue.Value;
            }

            if (tenantValue.HasValue && tenantValue.Value > 0)
            {
                return tenantValue.Value;
            }

            return hardDefault;
        }

        /// <summary>
        /// Determines whether the configured HTTP scale-out mode uses the runtime host manager.
        /// </summary>
        /// <param name="mode">The configured scale-out mode.</param>
        /// <returns><c>true</c> when host-manager mode is enabled; otherwise, <c>false</c>.</returns>
        private static bool IsHostManagerMode(string? mode)
        {
            return string.Equals(mode, AiHttpRuntimeScaleOutModes.HostManager, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates runtime metadata for the HTTP runtime registration and capacity descriptor.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The resolved tenant runtime settings.</param>
        /// <param name="tenantId">The resolved tenant id.</param>
        /// <param name="tenantGroupId">The resolved tenant group id.</param>
        /// <param name="isolationMode">The resolved isolation mode.</param>
        /// <param name="preferDedicatedCapacity">Whether dedicated capacity is preferred.</param>
        /// <param name="allowSharedFallback">Whether shared fallback is allowed.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <param name="endpoint">The HTTP endpoint.</param>
        /// <param name="workerCount">The resolved worker count.</param>
        /// <param name="maxConcurrentRuns">The resolved maximum concurrent runs.</param>
        /// <param name="queueCapacity">The resolved queue capacity.</param>
        /// <param name="maxRuntimeInstances">The resolved max runtime instances.</param>
        /// <returns>The metadata.</returns>
        private static Dictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings,
            string tenantId,
            string tenantGroupId,
            AiRuntimeInstanceIsolationMode isolationMode,
            bool preferDedicatedCapacity,
            bool allowSharedFallback,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix,
            string endpoint,
            int workerCount,
            int maxConcurrentRuns,
            int queueCapacity,
            int? maxRuntimeInstances)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, request.Metadata);
            CopyMetadata(metadata, tenantSettings.Metadata);

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = runtimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = endpoint;

            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenantId;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenantGroupId;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = isolationMode.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = preferDedicatedCapacity.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = allowSharedFallback.ToString();

            metadata["runtime.maxRuntimeInstances"] = maxRuntimeInstances?.ToString() ?? string.Empty;
            metadata["runtime.instanceIdPrefix"] = runtimeInstanceIdPrefix;
            metadata["runtime.workerCountPerInstance"] = workerCount.ToString();
            metadata["runtime.maxConcurrentRunsPerInstance"] = maxConcurrentRuns.ToString();
            metadata["runtime.localQueueCapacity"] = queueCapacity.ToString();

            metadata["scaleout.provider"] = ProviderName;
            metadata["scaleout.requestId"] = request.RequestId;
            metadata["scaleout.sharedRunId"] = request.SharedRunId;
            metadata["controlPlaneId"] = request.ControlPlaneId;

            return metadata;
        }

        /// <summary>
        /// Creates fulfilled metadata for host-manager scale-out.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="context">The resolved provisioning context.</param>
        /// <param name="startResult">The runtime host start result.</param>
        /// <param name="fulfilledRuntimeInstanceId">The fulfilled runtime instance id.</param>
        /// <param name="fulfilledTransportEndpoint">The fulfilled transport endpoint.</param>
        /// <returns>The fulfilled metadata.</returns>
        private static Dictionary<string, string> CreateFulfilledHostManagerMetadata(
            AiRuntimeScaleOutProviderRequest request,
            ProvisioningContext context,
            AiRuntimeHostStartResult startResult,
            string fulfilledRuntimeInstanceId,
            string fulfilledTransportEndpoint)
        {
            var metadata =
                new Dictionary<string, string>(
                    startResult.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, context.Metadata);

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = fulfilledRuntimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = fulfilledTransportEndpoint;

            metadata["scaleOutRequestId"] = request.RequestId;
            metadata["sharedRunId"] = request.SharedRunId;
            metadata["controlPlaneId"] = request.ControlPlaneId;
            metadata["hostCreation.mode"] = metadata.TryGetValue("hostCreation.mode", out var hostCreationMode)
                ? hostCreationMode
                : "HostManager";

            return metadata;
        }

        /// <summary>
        /// Copies metadata into the target dictionary.
        /// </summary>
        /// <param name="target">The target metadata dictionary.</param>
        /// <param name="source">The optional source metadata dictionary.</param>
        private static void CopyMetadata(
            IDictionary<string, string> target,
            IReadOnlyDictionary<string, string>? source)
        {
            if (source is null)
            {
                return;
            }

            foreach (var item in source.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
            {
                target[item.Key] = item.Value ?? string.Empty;
            }
        }

        /// <summary>
        /// Creates a fulfilled scale-out provider result.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="providerOperationId">The provider operation id.</param>
        /// <param name="message">The result message.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The fulfilled scale-out provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateFulfilledResult(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string providerOperationId,
            string message,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = true,
                Rejected = false,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderOperationId = providerOperationId,
                Message = message,
                Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId
                }
            };
        }

        /// <summary>
        /// Creates a rejected scale-out provider result.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="message">The message.</param>
        /// <returns>The rejected scale-out provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateRejectedResult(
            AiRuntimeScaleOutProviderRequest request,
            string failureReason,
            string message)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = false,
                Rejected = true,
                RuntimeInstanceId = null,
                ProviderOperationId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? "http-scaleout-rejected"
                    : $"http-scaleout-rejected-{request.RequestId}",
                FailureReason = failureReason,
                Message = message,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                    ["provider.name"] = ProviderName,
                    ["provider"] = ProviderName,
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId ?? string.Empty,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId ?? string.Empty
                }
            };
        }

        /// <summary>
        /// Holds resolved values required to provision one runtime instance.
        /// </summary>
        private sealed class ProvisioningContext
        {
            /// <summary>
            /// Gets the resolved tenant id.
            /// </summary>
            public required string TenantId { get; init; }

            /// <summary>
            /// Gets the resolved tenant group id.
            /// </summary>
            public required string TenantGroupId { get; init; }

            /// <summary>
            /// Gets the resolved isolation mode.
            /// </summary>
            public required AiRuntimeInstanceIsolationMode IsolationMode { get; init; }

            /// <summary>
            /// Gets a value indicating whether dedicated capacity is preferred.
            /// </summary>
            public required bool PreferDedicatedCapacity { get; init; }

            /// <summary>
            /// Gets a value indicating whether shared fallback is allowed.
            /// </summary>
            public required bool AllowSharedFallback { get; init; }

            /// <summary>
            /// Gets the resolved runtime instance id.
            /// </summary>
            public required string RuntimeInstanceId { get; init; }

            /// <summary>
            /// Gets the resolved runtime instance id prefix.
            /// </summary>
            public required string RuntimeInstanceIdPrefix { get; init; }

            /// <summary>
            /// Gets the resolved endpoint.
            /// </summary>
            public required string Endpoint { get; init; }

            /// <summary>
            /// Gets the resolved worker count.
            /// </summary>
            public required int WorkerCount { get; init; }

            /// <summary>
            /// Gets the resolved max concurrent runs.
            /// </summary>
            public required int MaxConcurrentRuns { get; init; }

            /// <summary>
            /// Gets the resolved queue capacity.
            /// </summary>
            public required int QueueCapacity { get; init; }

            /// <summary>
            /// Gets the resolved max runtime instances.
            /// </summary>
            public int? MaxRuntimeInstances { get; init; }

            /// <summary>
            /// Gets the resolved metadata.
            /// </summary>
            public required IReadOnlyDictionary<string, string> Metadata { get; init; }
        }
    }
}