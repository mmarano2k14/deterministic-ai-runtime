using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut
{
    /// <summary>
    /// Provisions gRPC runtime capacity for gRPC provider scale-out requests.
    /// </summary>
    public sealed class AiGrpcRuntimeScaleOutProvisioner : IAiGrpcRuntimeScaleOutProvisioner
    {
        private const string ProviderName = AiGrpcRuntimeProviderConstants.ProviderName;
        private const string DefaultRuntimeInstanceIdPrefix = "grpc-runtime";
        private const string DefaultEndpointTemplate = "http://localhost";
        private const int DefaultWorkerCountPerInstance = 1;
        private const int DefaultMaxConcurrentRunsPerInstance = 1;
        private const int DefaultQueueCapacity = 100;

        private readonly IAiRuntimeInstanceRegistry registry;
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;
        private readonly IAiRuntimeHostManager runtimeHostManager;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;
        private readonly IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider;
        private readonly AiGrpcRuntimeScaleOutOptions options;
        private readonly ILogger<AiGrpcRuntimeScaleOutProvisioner> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiGrpcRuntimeScaleOutProvisioner"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeHostManager">The runtime host manager.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        /// <param name="options">The gRPC scale-out technical options.</param>
        /// <param name="logger">The logger.</param>
        public AiGrpcRuntimeScaleOutProvisioner(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeHostManager runtimeHostManager,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider,
            IOptions<AiGrpcRuntimeScaleOutOptions> options,
            ILogger<AiGrpcRuntimeScaleOutProvisioner> logger)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.capacityStore = capacityStore ?? throw new ArgumentNullException(nameof(capacityStore));
            this.runtimeHostManager = runtimeHostManager ?? throw new ArgumentNullException(nameof(runtimeHostManager));
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
            this.tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
            ArgumentNullException.ThrowIfNull(options);
            this.options = options.Value ?? throw new ArgumentException("gRPC runtime scale-out options must be provided.", nameof(options));
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

            if (!options.Enabled)
            {
                return CreateRejectedResult(request, "grpc-runtime-scaleout-disabled", "gRPC runtime scale-out is disabled.");
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return CreateRejectedResult(request, "grpc-runtime-scaleout-request-id-missing", "gRPC runtime scale-out request id is missing.");
            }

            if (string.IsNullOrWhiteSpace(request.ControlPlaneId))
            {
                return CreateRejectedResult(request, "grpc-runtime-scaleout-control-plane-id-missing", "gRPC runtime scale-out control-plane id is missing.");
            }

            var context = CreateProvisioningContext(request);

            if (IsHostManagerMode(options.Mode))
            {
                return await ProvisionWithHostManagerAsync(request, context, startedAtUtc, cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                "GRPC SCALE-OUT PROVISION START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
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

            await registry.RegisterAsync(
                new AiRuntimeInstanceRegistration
                {
                    RuntimeInstanceId = context.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = $"grpc-scaleout-{request.ControlPlaneId}",
                    HostId = $"grpc-host-{context.RuntimeInstanceId}",
                    RuntimeId = context.RuntimeInstanceId,
                    TenantId = context.TenantId,
                    TenantGroupId = context.TenantGroupId,
                    Role = AiRuntimeInstanceRole.Runtime,
                    WorkerCount = context.WorkerCount,
                    MaxConcurrentRuns = context.MaxConcurrentRuns,
                    QueueCapacity = context.QueueCapacity,
                    RegisteredAtUtc = startedAtUtc,
                    Metadata = context.Metadata
                },
                cancellationToken).ConfigureAwait(false);

            await capacityStore.PublishAsync(
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = context.RuntimeInstanceId,
                    ControlPlaneId = request.ControlPlaneId,
                    ControlPlaneHostId = $"grpc-scaleout-{request.ControlPlaneId}",
                    TenantId = context.TenantId,
                    TenantGroupId = context.TenantGroupId,
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

            logger.LogInformation(
                "GRPC SCALE-OUT PROVISION FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint);

            return CreateFulfilledResult(request, context.RuntimeInstanceId, $"grpc-scaleout-{request.RequestId}", "gRPC runtime scale-out request was fulfilled.", context.Metadata);
        }

        /// <summary>
        /// Provisions gRPC runtime capacity by delegating lifecycle to the runtime host manager.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="context">The resolved provisioning context.</param>
        /// <param name="startedAtUtc">The provisioning start time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        private async Task<AiRuntimeScaleOutProviderResult> ProvisionWithHostManagerAsync(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            logger.LogInformation(
                "GRPC SCALE-OUT HOST-MANAGER START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                request.RequestId,
                request.SharedRunId,
                context.RuntimeInstanceId,
                context.Endpoint,
                options.HostCreationMode,
                context.TenantId,
                context.TenantGroupId,
                context.IsolationMode,
                context.WorkerCount,
                context.MaxConcurrentRuns,
                context.QueueCapacity);

            var startResult =
                await runtimeHostManager.StartRuntimeAsync(
                    new AiRuntimeHostStartRequest
                    {
                        RequestId = request.RequestId,
                        ControlPlaneId = request.ControlPlaneId,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = context.RuntimeInstanceId,
                        RuntimeInstanceIdPrefix = context.RuntimeInstanceIdPrefix,
                        ProviderName = ProviderName,
                        TransportName = AiGrpcRuntimeProviderConstants.TransportName,
                        TransportEndpoint = context.Endpoint,
                        HostCreationMode = options.HostCreationMode,
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
                logger.LogWarning(
                    "GRPC SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    context.RuntimeInstanceId,
                    options.HostCreationMode,
                    startResult.FailureReason);

                return CreateRejectedResult(request, startResult.FailureReason ?? "runtime-host-start-failed", "gRPC runtime scale-out host manager start failed.");
            }

            var fulfilledRuntimeInstanceId = !string.IsNullOrWhiteSpace(startResult.RuntimeInstanceId) ? startResult.RuntimeInstanceId : context.RuntimeInstanceId;
            var fulfilledTransportEndpoint = !string.IsNullOrWhiteSpace(startResult.TransportEndpoint) ? startResult.TransportEndpoint : context.Endpoint;

            if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
            {
                return CreateRejectedResult(request, "runtime-host-started-without-runtime-instance-id", "gRPC runtime scale-out host manager returned success without a runtime instance id.");
            }

            if (options.RequireReadiness)
            {
                var readinessResult =
                    await readinessWaiter.WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = fulfilledRuntimeInstanceId,
                            ProviderName = ProviderName,
                            TransportName = AiGrpcRuntimeProviderConstants.TransportName,
                            TransportEndpoint = fulfilledTransportEndpoint,
                            RequireTransportEndpoint = options.HostCreationMode != AiRuntimeHostCreationMode.Fixture,
                            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.ReadinessTimeoutSeconds)),
                            PollInterval = TimeSpan.FromMilliseconds(Math.Max(1, options.ReadinessPollIntervalMilliseconds))
                        },
                        cancellationToken).ConfigureAwait(false);

                if (!readinessResult.Success)
                {
                    logger.LogWarning(
                        "GRPC SCALE-OUT HOST-MANAGER READINESS FAILED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason} TimedOut={TimedOut}",
                        request.RequestId,
                        request.SharedRunId,
                        fulfilledRuntimeInstanceId,
                        options.HostCreationMode,
                        readinessResult.FailureReason,
                        readinessResult.TimedOut);

                    return CreateRejectedResult(request, readinessResult.FailureReason ?? "runtime-readiness-failed", "gRPC runtime scale-out readiness check failed.");
                }
            }

            var metadata = CreateFulfilledHostManagerMetadata(request, context, startResult, fulfilledRuntimeInstanceId, fulfilledTransportEndpoint);

            logger.LogInformation(
                "GRPC SCALE-OUT HOST-MANAGER FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} DurationMs={DurationMs}",
                request.RequestId,
                request.SharedRunId,
                fulfilledRuntimeInstanceId,
                fulfilledTransportEndpoint,
                options.HostCreationMode,
                (DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds);

            return CreateFulfilledResult(request, fulfilledRuntimeInstanceId, $"grpc-host-manager-scaleout-{request.RequestId}", "gRPC runtime scale-out request was fulfilled by the runtime host manager.", metadata);
        }

        /// <summary>
        /// Creates the tenant-aware provisioning context for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <returns>The resolved provisioning context.</returns>
        private AiRuntimeScaleOutProvisioningContext CreateProvisioningContext(
            AiRuntimeScaleOutProviderRequest request)
        {
            var tenantSettings = tenantRuntimeSettingsProvider.GetSettings(request.TenantId, request.TenantGroupId);
            var tenantId = ResolveText(request.TenantId, tenantSettings.TenantId, "shared");
            var tenantGroupId = ResolveText(request.TenantGroupId, tenantSettings.TenantGroupId, string.Empty);
            var isolationMode = ResolveIsolationMode(request, tenantSettings);
            var preferDedicatedCapacity = ResolveBoolean(request.PreferDedicatedCapacity, tenantSettings.PreferDedicatedCapacity);
            var allowSharedFallback = ResolveBoolean(request.AllowSharedFallback, tenantSettings.AllowSharedFallback);
            var runtimeInstanceIdPrefix = ResolveRuntimeInstanceIdPrefix(request, tenantSettings);
            var runtimeInstanceId = ResolveRuntimeInstanceId(request, runtimeInstanceIdPrefix);
            var endpoint = ResolveEndpoint(request, runtimeInstanceId, runtimeInstanceIdPrefix);
            var workerCount = ResolvePositiveOrDefault(tenantSettings.WorkerCountPerInstance, request.WorkerCountPerInstance, DefaultWorkerCountPerInstance);
            var maxConcurrentRuns = ResolvePositiveOrDefault(tenantSettings.MaxConcurrentRunsPerInstance, request.MaxConcurrentRunsPerInstance, DefaultMaxConcurrentRunsPerInstance);
            var queueCapacity = ResolvePositiveOrDefault(tenantSettings.LocalQueueCapacity, request.LocalQueueCapacity, DefaultQueueCapacity);
            var maxRuntimeInstances = ResolvePositiveOrNullableDefault(tenantSettings.MaxRuntimeInstances, request.MaxRuntimeInstances);

            var metadata = CreateMetadata(request, tenantSettings, tenantId, tenantGroupId, isolationMode, preferDedicatedCapacity, allowSharedFallback, runtimeInstanceId, runtimeInstanceIdPrefix, endpoint, workerCount, maxConcurrentRuns, queueCapacity, maxRuntimeInstances);

            return new AiRuntimeScaleOutProvisioningContext
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
        /// Resolves the tenant-aware runtime instance prefix from tenant settings, request, or gRPC technical options.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns>The runtime instance id prefix.</returns>
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

            if (!string.IsNullOrWhiteSpace(options.DefaultRuntimeInstanceIdPrefix))
            {
                return options.DefaultRuntimeInstanceIdPrefix.Trim();
            }

            return DefaultRuntimeInstanceIdPrefix;
        }

        /// <summary>
        /// Resolves the runtime instance id for the scale-out request.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <returns>The runtime instance id.</returns>
        private static string ResolveRuntimeInstanceId(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceIdPrefix)
        {
            var target = request.RequestedTargetInstanceCount > 0 ? request.RequestedTargetInstanceCount : Math.Max(1, request.CurrentInstanceCount + 1);
            return $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-{target}";
        }

        /// <summary>
        /// Resolves the gRPC endpoint for the newly materialized runtime instance.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <returns>The resolved endpoint.</returns>
        private string ResolveEndpoint(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix)
        {
            var endpointTemplate = string.IsNullOrWhiteSpace(options.EndpointTemplate) ? DefaultEndpointTemplate : options.EndpointTemplate.Trim();

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
        /// <returns>The runtime isolation mode.</returns>
        private static AiRuntimeInstanceIsolationMode ResolveIsolationMode(
            AiRuntimeScaleOutProviderRequest request,
            AiTenantRuntimeSettings tenantSettings)
        {
            return request.IsolationMode == default ? tenantSettings.IsolationMode : request.IsolationMode;
        }

        /// <summary>
        /// Resolves a boolean value using logical OR compatibility semantics.
        /// </summary>
        /// <param name="requestValue">The request value.</param>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <returns>The resolved value.</returns>
        private static bool ResolveBoolean(
            bool requestValue,
            bool tenantValue)
        {
            return requestValue || tenantValue;
        }

        /// <summary>
        /// Resolves the first non-empty text value.
        /// </summary>
        /// <param name="first">The first value.</param>
        /// <param name="second">The second value.</param>
        /// <param name="fallback">The fallback value.</param>
        /// <returns>The resolved value.</returns>
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
        /// Resolves the first positive integer value from tenant settings, request, or hard default.
        /// </summary>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <param name="requestValue">The request value.</param>
        /// <param name="hardDefault">The hard default value.</param>
        /// <returns>The resolved value.</returns>
        private static int ResolvePositiveOrDefault(
            int? tenantValue,
            int? requestValue,
            int hardDefault)
        {
            if (tenantValue.HasValue && tenantValue.Value > 0)
            {
                return tenantValue.Value;
            }

            if (requestValue.HasValue && requestValue.Value > 0)
            {
                return requestValue.Value;
            }

            return hardDefault;
        }

        /// <summary>
        /// Resolves the first positive nullable integer value from tenant settings or request.
        /// </summary>
        /// <param name="tenantValue">The tenant settings value.</param>
        /// <param name="requestValue">The request value.</param>
        /// <returns>The resolved value.</returns>
        private static int? ResolvePositiveOrNullableDefault(
            int tenantValue,
            int? requestValue)
        {
            if (tenantValue > 0)
            {
                return tenantValue;
            }

            if (requestValue.HasValue && requestValue.Value > 0)
            {
                return requestValue.Value;
            }

            return null;
        }

        /// <summary>
        /// Determines whether the configured gRPC scale-out mode uses the runtime host manager.
        /// </summary>
        /// <param name="mode">The configured scale-out mode.</param>
        /// <returns><see langword="true"/> when host-manager mode is enabled.</returns>
        private static bool IsHostManagerMode(
            string? mode)
        {
            return string.Equals(mode, AiGrpcRuntimeScaleOutModes.HostManager, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates runtime metadata for the gRPC runtime registration and capacity descriptor.
        /// </summary>
        /// <returns>The metadata dictionary.</returns>
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
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiGrpcRuntimeProviderConstants.TransportName;
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
        /// <param name="context">The provisioning context.</param>
        /// <param name="startResult">The host start result.</param>
        /// <param name="fulfilledRuntimeInstanceId">The fulfilled runtime instance id.</param>
        /// <param name="fulfilledTransportEndpoint">The fulfilled transport endpoint.</param>
        /// <returns>The metadata dictionary.</returns>
        private static Dictionary<string, string> CreateFulfilledHostManagerMetadata(
            AiRuntimeScaleOutProviderRequest request,
            AiRuntimeScaleOutProvisioningContext context,
            AiRuntimeHostStartResult startResult,
            string fulfilledRuntimeInstanceId,
            string fulfilledTransportEndpoint)
        {
            var metadata = new Dictionary<string, string>(startResult.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);

            CopyMetadata(metadata, context.Metadata);

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiGrpcRuntimeProviderConstants.TransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = fulfilledRuntimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = fulfilledTransportEndpoint;
            metadata["scaleOutRequestId"] = request.RequestId;
            metadata["sharedRunId"] = request.SharedRunId;
            metadata["controlPlaneId"] = request.ControlPlaneId;
            metadata["hostCreation.mode"] = metadata.TryGetValue("hostCreation.mode", out var hostCreationMode) ? hostCreationMode : "HostManager";

            return metadata;
        }

        /// <summary>
        /// Copies metadata into the target dictionary.
        /// </summary>
        /// <param name="target">The target metadata dictionary.</param>
        /// <param name="source">The source metadata dictionary.</param>
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
        /// <returns>The scale-out provider result.</returns>
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
        /// <param name="message">The result message.</param>
        /// <returns>The scale-out provider result.</returns>
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
                ProviderOperationId = string.IsNullOrWhiteSpace(request.RequestId) ? "grpc-scaleout-rejected" : $"grpc-scaleout-rejected-{request.RequestId}",
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
    }
}
