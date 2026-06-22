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
    /// This provisioner does not resolve tenant runtime settings by itself.
    /// Tenant-aware runtime settings must already be resolved by admission and carried
    /// through <see cref="AiRuntimeScaleOutProviderRequest" />.
    ///
    /// HTTP scale-out options are provider technical defaults only.
    /// </remarks>
    public sealed class AiHttpRuntimeScaleOutProvisioner :
        IAiHttpRuntimeScaleOutProvisioner
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

        /// <summary>
        /// Runtime instance registry.
        /// </summary>
        private readonly IAiRuntimeInstanceRegistry registry;

        /// <summary>
        /// Runtime instance capacity store.
        /// </summary>
        private readonly IAiRuntimeInstanceCapacityStore capacityStore;

        /// <summary>
        /// Runtime host manager used by host-manager scale-out mode.
        /// </summary>
        private readonly IAiRuntimeHostManager runtimeHostManager;

        /// <summary>
        /// Runtime instance readiness waiter used by host-manager scale-out mode.
        /// </summary>
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;

        /// <summary>
        /// HTTP scale-out technical options.
        /// </summary>
        private readonly AiHttpRuntimeScaleOutOptions options;

        /// <summary>
        /// Logger used for HTTP scale-out diagnostics.
        /// </summary>
        private readonly ILogger<AiHttpRuntimeScaleOutProvisioner> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiHttpRuntimeScaleOutProvisioner"/> class.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The runtime instance capacity store.</param>
        /// <param name="runtimeHostManager">The runtime host manager.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        /// <param name="options">The HTTP scale-out technical options.</param>
        /// <param name="logger">The logger.</param>
        public AiHttpRuntimeScaleOutProvisioner(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeHostManager runtimeHostManager,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter,
            IOptions<AiHttpRuntimeScaleOutOptions> options,
            ILogger<AiHttpRuntimeScaleOutProvisioner> logger)
        {
            this.registry =
                registry
                ?? throw new ArgumentNullException(nameof(registry));

            this.capacityStore =
                capacityStore
                ?? throw new ArgumentNullException(nameof(capacityStore));

            this.runtimeHostManager =
                runtimeHostManager
                ?? throw new ArgumentNullException(nameof(runtimeHostManager));

            this.readinessWaiter =
                readinessWaiter
                ?? throw new ArgumentNullException(nameof(readinessWaiter));

            ArgumentNullException.ThrowIfNull(options);

            this.options =
                options.Value
                ?? throw new ArgumentException(
                    "HTTP runtime scale-out options must be provided.",
                    nameof(options));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> ProvisionAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            if (!this.options.Enabled)
            {
                return CreateRejectedResult(
                    request,
                    "http-runtime-scaleout-disabled",
                    "HTTP runtime scale-out is disabled.");
            }

            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return CreateRejectedResult(
                    request,
                    "http-runtime-scaleout-request-id-missing",
                    "HTTP runtime scale-out request id is missing.");
            }

            if (string.IsNullOrWhiteSpace(request.ControlPlaneId))
            {
                return CreateRejectedResult(
                    request,
                    "http-runtime-scaleout-control-plane-id-missing",
                    "HTTP runtime scale-out control-plane id is missing.");
            }

            if (IsHostManagerMode(this.options.Mode))
            {
                return await this
                    .ProvisionWithHostManagerAsync(
                        request,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var runtimeInstanceIdPrefix =
                ResolveRuntimeInstanceIdPrefix(
                    request);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    request,
                    runtimeInstanceIdPrefix);

            var endpoint =
                ResolveEndpoint(
                    request,
                    runtimeInstanceId);

            var workerCount =
                ResolvePositiveOrDefault(
                    request.WorkerCountPerInstance,
                    DefaultWorkerCountPerInstance);

            var maxConcurrentRuns =
                ResolvePositiveOrDefault(
                    request.MaxConcurrentRunsPerInstance,
                    DefaultMaxConcurrentRunsPerInstance);

            var queueCapacity =
                ResolvePositiveOrDefault(
                    request.LocalQueueCapacity,
                    DefaultQueueCapacity);

            var metadata =
                CreateMetadata(
                    request,
                    runtimeInstanceId,
                    runtimeInstanceIdPrefix,
                    endpoint,
                    workerCount,
                    maxConcurrentRuns,
                    queueCapacity);

            this.logger.LogInformation(
                "HTTP SCALE-OUT PROVISION START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                request.RequestId,
                request.SharedRunId,
                runtimeInstanceId,
                endpoint,
                request.TenantId,
                request.TenantGroupId,
                request.IsolationMode,
                workerCount,
                maxConcurrentRuns,
                queueCapacity);

            await this.registry
                .RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        ControlPlaneId = request.ControlPlaneId,
                        ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                        HostId = $"http-host-{runtimeInstanceId}",
                        RuntimeId = runtimeInstanceId,
                        Role = AiRuntimeInstanceRole.Runtime,
                        WorkerCount = workerCount,
                        MaxConcurrentRuns = maxConcurrentRuns,
                        QueueCapacity = queueCapacity,
                        RegisteredAtUtc = startedAtUtc,
                        Metadata = metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await this.capacityStore
                .PublishAsync(
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        ControlPlaneId = request.ControlPlaneId,
                        ControlPlaneHostId = $"http-scaleout-{request.ControlPlaneId}",
                        Role = AiRuntimeInstanceRole.Runtime,
                        Status = AiRuntimeInstanceStatus.Ready,
                        WorkerCount = workerCount,
                        ActiveWorkerCount = 0,
                        AvailableWorkerCount = workerCount,
                        MaxWorkersPerRun = workerCount,
                        MinWorkersRequiredPerRun = 1,
                        QueuedRunCount = 0,
                        RunningRunCount = 0,
                        ActiveRunCount = 0,
                        MaxConcurrentRuns = maxConcurrentRuns,
                        MaxRunSlots = maxConcurrentRuns,
                        AvailableRunSlots = maxConcurrentRuns,
                        ReservedRunSlots = 0,
                        EffectiveAvailableRunSlots = maxConcurrentRuns,
                        IsQueuePaused = false,
                        CanAcceptRun = true,
                        LastHeartbeatAtUtc = startedAtUtc,
                        Metadata = metadata
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "HTTP SCALE-OUT PROVISION FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint}",
                request.RequestId,
                request.SharedRunId,
                runtimeInstanceId,
                endpoint);

            return new AiRuntimeScaleOutProviderResult
            {
                Success = true,
                Rejected = false,
                RuntimeInstanceId = runtimeInstanceId,
                ProviderOperationId = $"http-scaleout-{request.RequestId}",
                Message = "HTTP runtime scale-out request was fulfilled.",
                Metadata = new Dictionary<string, string>(
                    metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId
                }
            };
        }

        /// <summary>
        /// Provisions HTTP runtime capacity by delegating runtime lifecycle to the runtime host manager.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="startedAtUtc">The UTC timestamp when provisioning started.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out provider result.</returns>
        private async Task<AiRuntimeScaleOutProviderResult> ProvisionWithHostManagerAsync(
            AiRuntimeScaleOutProviderRequest request,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var runtimeInstanceIdPrefix =
                ResolveRuntimeInstanceIdPrefix(
                    request);

            var runtimeInstanceId =
                ResolveRuntimeInstanceId(
                    request,
                    runtimeInstanceIdPrefix);

            var endpoint =
                ResolveEndpoint(
                    request,
                    runtimeInstanceId);

            var workerCount =
                ResolvePositiveOrDefault(
                    request.WorkerCountPerInstance,
                    DefaultWorkerCountPerInstance);

            var maxConcurrentRuns =
                ResolvePositiveOrDefault(
                    request.MaxConcurrentRunsPerInstance,
                    DefaultMaxConcurrentRunsPerInstance);

            var queueCapacity =
                ResolvePositiveOrDefault(
                    request.LocalQueueCapacity,
                    DefaultQueueCapacity);

            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER START RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} TenantId={TenantId} TenantGroupId={TenantGroupId} IsolationMode={IsolationMode} WorkerCount={WorkerCount} MaxConcurrentRuns={MaxConcurrentRuns} QueueCapacity={QueueCapacity}",
                request.RequestId,
                request.SharedRunId,
                runtimeInstanceId,
                endpoint,
                this.options.HostCreationMode,
                request.TenantId,
                request.TenantGroupId,
                request.IsolationMode,
                workerCount,
                maxConcurrentRuns,
                queueCapacity);

            var startResult =
                await this.runtimeHostManager
                    .StartRuntimeAsync(
                        new AiRuntimeHostStartRequest
                        {
                            RequestId = request.RequestId,
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = runtimeInstanceId,
                            RuntimeInstanceIdPrefix = runtimeInstanceIdPrefix,
                            ProviderName = ProviderName,
                            TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                            TransportEndpoint = endpoint,
                            HostCreationMode = this.options.HostCreationMode,
                            TenantId = request.TenantId,
                            TenantGroupId = request.TenantGroupId,
                            IsolationMode = request.IsolationMode.ToString(),
                            PreferDedicatedCapacity = request.PreferDedicatedCapacity,
                            AllowSharedFallback = request.AllowSharedFallback,
                            WorkerCountPerInstance = workerCount,
                            MaxConcurrentRunsPerInstance = maxConcurrentRuns,
                            LocalQueueCapacity = queueCapacity,
                            MaxRuntimeInstances = request.MaxRuntimeInstances
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!startResult.Success)
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    runtimeInstanceId,
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
                    : runtimeInstanceId;

            var fulfilledTransportEndpoint =
                !string.IsNullOrWhiteSpace(startResult.TransportEndpoint)
                    ? startResult.TransportEndpoint
                    : endpoint;

            if (string.IsNullOrWhiteSpace(fulfilledRuntimeInstanceId))
            {
                this.logger.LogWarning(
                    "HTTP SCALE-OUT HOST-MANAGER REJECTED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} HostCreationMode={HostCreationMode} FailureReason={FailureReason}",
                    request.RequestId,
                    request.SharedRunId,
                    runtimeInstanceId,
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
                    await this.readinessWaiter
                        .WaitUntilReadyAsync(
                            new AiRuntimeInstanceReadinessRequest
                            {
                                ControlPlaneId = request.ControlPlaneId,
                                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                                RuntimeInstanceId = fulfilledRuntimeInstanceId,
                                ProviderName = ProviderName,
                                TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                                TransportEndpoint = fulfilledTransportEndpoint,
                                RequireTransportEndpoint = this.options.HostCreationMode != AiRuntimeHostCreationMode.Fixture,
                                Timeout = TimeSpan.FromSeconds(
                                    Math.Max(
                                        1,
                                        this.options.ReadinessTimeoutSeconds)),
                                PollInterval = TimeSpan.FromMilliseconds(
                                    Math.Max(
                                        1,
                                        this.options.ReadinessPollIntervalMilliseconds))
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

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
                new Dictionary<string, string>(
                    startResult.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                    ["provider.name"] = ProviderName,
                    ["provider"] = ProviderName,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = fulfilledRuntimeInstanceId,
                    [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = fulfilledTransportEndpoint,
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId ?? string.Empty,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId ?? string.Empty,
                    ["hostCreation.mode"] = this.options.HostCreationMode.ToString()
                };

            this.logger.LogInformation(
                "HTTP SCALE-OUT HOST-MANAGER FULFILLED RequestId={RequestId} SharedRunId={SharedRunId} RuntimeInstanceId={RuntimeInstanceId} Endpoint={Endpoint} HostCreationMode={HostCreationMode} DurationMs={DurationMs}",
                request.RequestId,
                request.SharedRunId,
                fulfilledRuntimeInstanceId,
                fulfilledTransportEndpoint,
                this.options.HostCreationMode,
                (DateTimeOffset.UtcNow - startedAtUtc).TotalMilliseconds);

            return new AiRuntimeScaleOutProviderResult
            {
                Success = true,
                Rejected = false,
                RuntimeInstanceId = fulfilledRuntimeInstanceId,
                ProviderOperationId = $"http-host-manager-scaleout-{request.RequestId}",
                Message = "HTTP runtime scale-out request was fulfilled by the runtime host manager.",
                Metadata = metadata
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
                Metadata = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
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
        /// Resolves the tenant-aware runtime instance prefix from the request, falling back to HTTP technical options.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <returns>The runtime instance id prefix.</returns>
        private string ResolveRuntimeInstanceIdPrefix(
            AiRuntimeScaleOutProviderRequest request)
        {
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
                    : Math.Max(
                        1,
                        request.CurrentInstanceCount + 1);

            return $"{request.ControlPlaneId}:{runtimeInstanceIdPrefix}-{target}";
        }

        /// <summary>
        /// Resolves the HTTP endpoint for the newly materialized runtime instance.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The HTTP endpoint.</returns>
        private string ResolveEndpoint(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId)
        {
            var endpointTemplate =
                string.IsNullOrWhiteSpace(this.options.EndpointTemplate)
                    ? DefaultEndpointTemplate
                    : this.options.EndpointTemplate.Trim();

            return endpointTemplate
                .Replace(
                    "{runtimeInstanceId}",
                    runtimeInstanceId,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "{tenantId}",
                    request.TenantId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "{tenantGroupId}",
                    request.TenantGroupId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "{controlPlaneId}",
                    request.ControlPlaneId,
                    StringComparison.OrdinalIgnoreCase)
                .Replace(
                    "{runtimeInstanceIdPrefix}",
                    ResolveRuntimeInstanceIdPrefix(request),
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a positive integer value with a hard fallback.
        /// </summary>
        /// <param name="value">The optional value.</param>
        /// <param name="hardDefault">The hard fallback value.</param>
        /// <returns>The resolved positive value.</returns>
        private static int ResolvePositiveOrDefault(
            int? value,
            int hardDefault)
        {
            if (value.HasValue &&
                value.Value > 0)
            {
                return value.Value;
            }

            return hardDefault;
        }

        /// <summary>
        /// Determines whether the configured HTTP scale-out mode uses the runtime host manager.
        /// </summary>
        /// <param name="mode">The configured scale-out mode.</param>
        /// <returns><c>true</c> when host-manager mode is enabled; otherwise, <c>false</c>.</returns>
        private static bool IsHostManagerMode(
            string? mode)
        {
            return string.Equals(
                mode,
                AiHttpRuntimeScaleOutModes.HostManager,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates runtime metadata for the HTTP runtime registration and capacity descriptor.
        /// </summary>
        /// <param name="request">The scale-out provider request.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="runtimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <param name="endpoint">The HTTP endpoint.</param>
        /// <param name="workerCount">The resolved worker count.</param>
        /// <param name="maxConcurrentRuns">The resolved maximum concurrent runs.</param>
        /// <param name="queueCapacity">The resolved queue capacity.</param>
        /// <returns>The metadata.</returns>
        private static Dictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceId,
            string runtimeInstanceIdPrefix,
            string endpoint,
            int workerCount,
            int maxConcurrentRuns,
            int queueCapacity)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (request.Metadata is not null)
            {
                foreach (var item in request.Metadata.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
                {
                    metadata[item.Key] = item.Value ?? string.Empty;
                }
            }

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName;
            metadata["provider.name"] = ProviderName;
            metadata["provider"] = ProviderName;

            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] = runtimeInstanceId;
            metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = endpoint;

            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId ?? string.Empty;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId ?? string.Empty;
            metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = request.PreferDedicatedCapacity.ToString();
            metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = request.AllowSharedFallback.ToString();

            metadata["runtime.maxRuntimeInstances"] = request.MaxRuntimeInstances?.ToString() ?? string.Empty;
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
    }
}