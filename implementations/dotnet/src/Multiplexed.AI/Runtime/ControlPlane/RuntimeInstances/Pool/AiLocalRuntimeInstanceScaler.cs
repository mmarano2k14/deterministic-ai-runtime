using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using System.Globalization;

namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Creates, starts, registers, stops, unregisters, and disposes local runtime instance hosts.
    /// </summary>
    /// <remarks>
    /// This scaler is the single lifecycle owner for local runtime instance hosts.
    /// It is used by the local runtime instance pool hosted service at startup and
    /// by the local runtime instance provider during dynamic scale-out.
    ///
    /// IMPORTANT:
    /// - <see cref="AiLocalRuntimeInstancePoolOptions.Enabled" /> controls startup pool creation.
    /// - It must not block dynamic scale-out requests.
    /// - Dynamic scale-out is controlled by admission and provider selection.
    /// - Dynamic scale-out targets are scoped by the resolved runtime instance id prefix,
    ///   not by the global number of local runtime hosts in the process.
    /// </remarks>
    public sealed class AiLocalRuntimeInstanceScaler :
        IAiLocalRuntimeInstanceScaler
    {
        private const string ProviderName = "local";
        private const string UnknownHostMode = "(unknown)";
        private const string UnknownEnableSharedQueuePump = "(unknown)";

        private readonly IAiLocalRuntimeInstanceHostFactory hostFactory;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiRuntimeHostIdentity runtimeHostIdentity;
        private readonly AiLocalRuntimeInstancePoolOptions options;
        private readonly IConfiguration configuration;
        private readonly ILogger<AiLocalRuntimeInstanceScaler> logger;
        private readonly List<IAiLocalRuntimeInstanceHost> hosts = new();
        private readonly SemaphoreSlim gate = new(1, 1);

        private int stopRequested;
        private int disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiLocalRuntimeInstanceScaler" /> class.
        /// </summary>
        /// <param name="hostFactory">The local runtime instance host factory.</param>
        /// <param name="sharedRuntimeInstanceRegistry">The shared runtime instance registry.</param>
        /// <param name="runtimeHostIdentity">The runtime host identity.</param>
        /// <param name="options">The local runtime instance pool options.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="logger">The logger.</param>
        public AiLocalRuntimeInstanceScaler(
            IAiLocalRuntimeInstanceHostFactory hostFactory,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeHostIdentity runtimeHostIdentity,
            IOptions<AiLocalRuntimeInstancePoolOptions> options,
            IConfiguration configuration,
            ILogger<AiLocalRuntimeInstanceScaler> logger)
        {
            this.hostFactory =
                hostFactory
                ?? throw new ArgumentNullException(nameof(hostFactory));

            this.sharedRuntimeInstanceRegistry =
                sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.runtimeHostIdentity =
                runtimeHostIdentity
                ?? throw new ArgumentNullException(nameof(runtimeHostIdentity));

            this.options =
                options?.Value
                ?? throw new ArgumentNullException(nameof(options));

            this.configuration =
                configuration
                ?? throw new ArgumentNullException(nameof(configuration));

            this.logger =
                logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public int ActiveInstanceCount => this.hosts.Count;

        /// <inheritdoc />
        public async Task<AiRuntimeScaleOutProviderResult> EnsureCapacityAsync(
            AiRuntimeScaleOutProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref this.disposed) == 1,
                this);

            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc =
                DateTimeOffset.UtcNow;

            ValidateOptions();

            var settings =
                ResolveScaleOutSettings(request);

            ValidateScaleOutSettings(settings);

            await this.gate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref this.disposed) == 1,
                    this);

                cancellationToken.ThrowIfCancellationRequested();

                var targetInstanceCount =
                    ResolveTargetInstanceCount(request);

                var matchingHosts =
                    this.GetMatchingHosts(
                            settings)
                        .ToList();

                if (targetInstanceCount <= matchingHosts.Count)
                {
                    return new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = matchingHosts.LastOrDefault()?.RuntimeInstanceId,
                        ProviderOperationId = $"local-scaleout-noop-{request.RequestId}",
                        Message = $"Local runtime instance capacity already satisfies scoped target '{targetInstanceCount}'.",
                        Metadata = CreateMetadata(
                            request,
                            "noop",
                            matchingHosts.Count,
                            targetInstanceCount,
                            createdInstanceCount: 0)
                    };
                }

                var createdInstanceCount = 0;
                IAiLocalRuntimeInstanceHost? lastCreatedHost = null;

                while (matchingHosts.Count < targetInstanceCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var index =
                        matchingHosts.Count + 1;

                    var host =
                        await this.CreateStartAndRegisterHostAsync(
                                index,
                                targetInstanceCount,
                                settings,
                                cancellationToken)
                            .ConfigureAwait(false);

                    this.hosts.Add(host);
                    matchingHosts.Add(host);

                    lastCreatedHost = host;
                    createdInstanceCount++;
                }

                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                this.logger.LogInformation(
                    "Local runtime instance scale-out fulfilled. HostId={HostId}, RequestId={RequestId}, SharedRunId={SharedRunId}, TenantId={TenantId}, TenantGroupId={TenantGroupId}, IsolationMode={IsolationMode}, ActiveInstanceCount={ActiveInstanceCount}, ScopedActiveInstanceCount={ScopedActiveInstanceCount}, TargetInstanceCount={TargetInstanceCount}, CreatedInstanceCount={CreatedInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, DurationMs={DurationMs}",
                    this.runtimeHostIdentity.HostId,
                    request.RequestId,
                    request.SharedRunId,
                    request.TenantId,
                    request.TenantGroupId,
                    request.IsolationMode,
                    this.hosts.Count,
                    matchingHosts.Count,
                    targetInstanceCount,
                    createdInstanceCount,
                    settings.RuntimeInstanceIdPrefix,
                    settings.WorkerCountPerInstance,
                    settings.MaxConcurrentRunsPerInstance,
                    settings.LocalQueueCapacity?.ToString(CultureInfo.InvariantCulture) ?? "unlimited",
                    Math.Max(0, (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeScaleOutProviderResult
                {
                    Success = true,
                    Rejected = false,
                    RuntimeInstanceId = lastCreatedHost?.RuntimeInstanceId ?? matchingHosts.LastOrDefault()?.RuntimeInstanceId,
                    ProviderOperationId = $"local-scaleout-{request.RequestId}",
                    Message = $"Local runtime instance scale-out fulfilled. Created {createdInstanceCount} runtime instance(s).",
                    Metadata = CreateMetadata(
                        request,
                        "fulfilled",
                        matchingHosts.Count,
                        targetInstanceCount,
                        createdInstanceCount)
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogWarning(
                    exception,
                    "Local runtime instance scale-out failed. HostId={HostId}, RequestId={RequestId}, SharedRunId={SharedRunId}, TenantId={TenantId}, ActiveInstanceCount={ActiveInstanceCount}",
                    this.runtimeHostIdentity.HostId,
                    request.RequestId,
                    request.SharedRunId,
                    request.TenantId,
                    this.hosts.Count);

                return CreateRejectedResult(
                    request,
                    "local-runtime-instance-scaleout-failed",
                    exception.Message);
            }
            finally
            {
                this.gate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref this.disposed) == 1)
            {
                return;
            }

            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            if (Interlocked.Exchange(ref this.stopRequested, 1) == 1)
            {
                this.logger.LogInformation(
                    "Local runtime instance scaler stop skipped because stop is already requested. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    this.hosts.Count,
                    this.options.RuntimeInstanceIdPrefix);

                return;
            }

            await this.gate
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                if (Volatile.Read(ref this.disposed) == 1)
                {
                    return;
                }

                foreach (var host in this.hosts.AsEnumerable().Reverse())
                {
                    this.logger.LogInformation(
                        "Stopping local runtime instance. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                        hostMode,
                        enableSharedQueuePump,
                        this.runtimeHostIdentity.HostId,
                        host.RuntimeInstanceId);

                    try
                    {
                        await host
                            .StopAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.logger.LogWarning(
                            exception,
                            "Local runtime instance stop failed during scaler shutdown. Continuing cleanup best-effort. HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                            this.runtimeHostIdentity.HostId,
                            host.RuntimeInstanceId);
                    }

                    try
                    {
                        await this.sharedRuntimeInstanceRegistry
                            .UnregisterAsync(
                                host.RuntimeInstanceId,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.logger.LogWarning(
                            exception,
                            "Local runtime instance shared registry unregister failed during scaler shutdown. HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                            this.runtimeHostIdentity.HostId,
                            host.RuntimeInstanceId);
                    }
                }

                this.logger.LogInformation(
                    "Local runtime instance scaler stopped. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    this.options.RuntimeInstanceIdPrefix);
            }
            finally
            {
                this.gate.Release();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            {
                return;
            }

            await this.gate
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                foreach (var host in this.hosts.AsEnumerable().Reverse())
                {
                    try
                    {
                        await host
                            .DisposeAsync()
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        this.logger.LogWarning(
                            exception,
                            "Local runtime instance dispose failed during scaler disposal. HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                            this.runtimeHostIdentity.HostId,
                            host.RuntimeInstanceId);
                    }
                }

                this.hosts.Clear();
            }
            finally
            {
                this.gate.Release();
                this.gate.Dispose();
            }
        }

        /// <summary>
        /// Creates, starts, and registers one local runtime instance host.
        /// </summary>
        /// <param name="index">The runtime instance index within the requested scale-out scope.</param>
        /// <param name="targetInstanceCount">The target number of active runtime instances within the requested scope.</param>
        /// <param name="settings">The resolved scale-out settings to apply to the created runtime instance.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The started local runtime instance host.</returns>
        private async Task<IAiLocalRuntimeInstanceHost> CreateStartAndRegisterHostAsync(
            int index,
            int targetInstanceCount,
            LocalRuntimeInstanceScaleOutSettings settings,
            CancellationToken cancellationToken)
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            var runtimeId =
                $"{settings.RuntimeInstanceIdPrefix}-{index}";

            var runtimeInstanceId =
                CreateRuntimeInstanceId(
                    this.runtimeHostIdentity.HostId,
                    runtimeId);

            this.logger.LogInformation(
                "Creating local runtime instance host. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, Index={Index}, TargetInstanceCount={TargetInstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, MetadataCount={MetadataCount}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                runtimeId,
                runtimeInstanceId,
                index,
                targetInstanceCount,
                settings.WorkerCountPerInstance,
                settings.MaxConcurrentRunsPerInstance,
                settings.LocalQueueCapacity?.ToString(CultureInfo.InvariantCulture) ?? "unlimited",
                settings.Metadata.Count);

            var host =
                await this.hostFactory
                    .CreateAsync(
                        runtimeInstanceId,
                        settings.WorkerCountPerInstance,
                        settings.MaxConcurrentRunsPerInstance,
                        settings.LocalQueueCapacity,
                        settings.Metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

            await this.sharedRuntimeInstanceRegistry
                .RegisterAsync(
                    host.SharedRuntimeInstance,
                    cancellationToken)
                .ConfigureAwait(false);

            await host
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "Local runtime instance started and registered. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}, ActiveInstanceCount={ActiveInstanceCount}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                runtimeId,
                host.RuntimeInstanceId,
                host.WorkerCount,
                this.hosts.Count + 1);

            return host;
        }

        /// <summary>
        /// Resolves the effective local runtime instance scale-out settings for a request.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <returns>The resolved local runtime instance scale-out settings.</returns>
        private LocalRuntimeInstanceScaleOutSettings ResolveScaleOutSettings(
            AiRuntimeScaleOutProviderRequest request)
        {
            var runtimeInstanceIdPrefix =
                string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix)
                    ? this.options.RuntimeInstanceIdPrefix
                    : request.RuntimeInstanceIdPrefix.Trim();

            var workerCountPerInstance =
                request.WorkerCountPerInstance.GetValueOrDefault(
                    this.options.WorkerCountPerInstance);

            var maxConcurrentRunsPerInstance =
                request.MaxConcurrentRunsPerInstance.GetValueOrDefault(
                    this.options.MaxConcurrentRunsPerInstance);

            var localQueueCapacity =
                request.LocalQueueCapacity ?? this.options.LocalQueueCapacity;

            var metadata =
                CreateHostMetadata(
                    request,
                    runtimeInstanceIdPrefix,
                    workerCountPerInstance,
                    maxConcurrentRunsPerInstance,
                    localQueueCapacity);

            return new LocalRuntimeInstanceScaleOutSettings(
                runtimeInstanceIdPrefix,
                workerCountPerInstance,
                maxConcurrentRunsPerInstance,
                localQueueCapacity,
                metadata);
        }

        /// <summary>
        /// Creates the metadata copied into local runtime instance registration and capacity descriptors.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <param name="runtimeInstanceIdPrefix">The resolved runtime instance id prefix.</param>
        /// <param name="workerCountPerInstance">The resolved worker count per instance.</param>
        /// <param name="maxConcurrentRunsPerInstance">The resolved maximum concurrent run count per instance.</param>
        /// <param name="localQueueCapacity">The resolved local queue capacity.</param>
        /// <returns>The metadata dictionary.</returns>
        private IReadOnlyDictionary<string, string> CreateHostMetadata(
            AiRuntimeScaleOutProviderRequest request,
            string runtimeInstanceIdPrefix,
            int workerCountPerInstance,
            int maxConcurrentRunsPerInstance,
            int? localQueueCapacity)
        {
            var metadata =
                new Dictionary<string, string>(
                    this.options.Metadata,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in request.Metadata ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    metadata[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName] =
                metadata.TryGetValue(AiRuntimeInstanceProviderMetadataKeys.ProviderName, out var existingProviderName) &&
                !string.IsNullOrWhiteSpace(existingProviderName)
                    ? existingProviderName
                    : ProviderName;

            metadata["provider"] =
                metadata.TryGetValue("provider", out var existingProvider) &&
                !string.IsNullOrWhiteSpace(existingProvider)
                    ? existingProvider
                    : metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName];

            metadata["runtime.scaler.provider"] = ProviderName;

            metadata["runtime.instanceIdPrefix"] = runtimeInstanceIdPrefix;
            metadata["runtime.workerCountPerInstance"] =
                workerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            metadata["runtime.maxConcurrentRunsPerInstance"] =
                maxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);

            if (localQueueCapacity.HasValue)
            {
                metadata["runtime.localQueueCapacity"] =
                    localQueueCapacity.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
            }

            if (HasExplicitTenantRuntimeSettings(request))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] =
                    request.IsolationMode.ToString();

                metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] =
                    request.PreferDedicatedCapacity.ToString();

                metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] =
                    request.AllowSharedFallback.ToString();

                if (request.MaxRuntimeInstances.HasValue)
                {
                    metadata["runtime.maxRuntimeInstances"] =
                        request.MaxRuntimeInstances.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            return metadata;
        }

        /// <summary>
        /// Determines whether a scale-out request carries explicit tenant runtime settings.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <returns>
        /// <see langword="true"/> when the request carries tenant/runtime-specific settings
        /// that should override pool metadata; otherwise, <see langword="false"/>.
        /// </returns>
        private static bool HasExplicitTenantRuntimeSettings(
            AiRuntimeScaleOutProviderRequest request)
        {
            return !string.IsNullOrWhiteSpace(request.TenantGroupId) ||
                   !string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix) ||
                   request.MaxRuntimeInstances.HasValue ||
                   request.WorkerCountPerInstance.HasValue ||
                   request.MaxConcurrentRunsPerInstance.HasValue ||
                   request.LocalQueueCapacity.HasValue ||
                   request.PreferDedicatedCapacity ||
                   !request.AllowSharedFallback ||
                   request.IsolationMode != AiRuntimeInstanceIsolationMode.Shared;
        }

        /// <summary>
        /// Resolves the target runtime instance count for the current scale-out request.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <returns>The target runtime instance count within the requested scope.</returns>
        private static int ResolveTargetInstanceCount(
            AiRuntimeScaleOutProviderRequest request)
        {
            var requestedTarget =
                request.RequestedTargetInstanceCount > 0
                    ? request.RequestedTargetInstanceCount
                    : Math.Max(request.CurrentInstanceCount + 1, 1);

            if (request.MaxInstanceCount.HasValue)
            {
                requestedTarget =
                    Math.Min(
                        requestedTarget,
                        request.MaxInstanceCount.Value);
            }

            return Math.Max(
                requestedTarget,
                1);
        }

        /// <summary>
        /// Gets the active local runtime hosts matching the scale-out request scope.
        /// </summary>
        /// <param name="settings">The resolved scale-out settings.</param>
        /// <returns>The matching local runtime hosts.</returns>
        private IEnumerable<IAiLocalRuntimeInstanceHost> GetMatchingHosts(
            LocalRuntimeInstanceScaleOutSettings settings)
        {
            foreach (var host in this.hosts)
            {
                if (IsMatchingHost(
                        host,
                        settings))
                {
                    yield return host;
                }
            }
        }

        /// <summary>
        /// Determines whether an existing local runtime host belongs to the requested scale-out scope.
        /// </summary>
        /// <param name="host">The local runtime host.</param>
        /// <param name="settings">The resolved scale-out settings.</param>
        /// <returns><see langword="true"/> when the host belongs to the requested scope; otherwise, <see langword="false"/>.</returns>
        private static bool IsMatchingHost(
            IAiLocalRuntimeInstanceHost host,
            LocalRuntimeInstanceScaleOutSettings settings)
        {
            if (host is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(settings.RuntimeInstanceIdPrefix))
            {
                return false;
            }

            return host.RuntimeInstanceId.Contains(
                $":{settings.RuntimeInstanceIdPrefix}-",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates a rejected scale-out provider result.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <param name="failureReason">The failure reason code.</param>
        /// <param name="message">The failure message.</param>
        /// <returns>The rejected provider result.</returns>
        private static AiRuntimeScaleOutProviderResult CreateRejectedResult(
            AiRuntimeScaleOutProviderRequest request,
            string failureReason,
            string message)
        {
            return new AiRuntimeScaleOutProviderResult
            {
                Success = false,
                Rejected = true,
                FailureReason = failureReason,
                Message = message,
                ProviderOperationId = $"local-scaleout-rejected-{request.RequestId}",
                Metadata = CreateMetadata(
                    request,
                    "rejected",
                    activeInstanceCount: 0,
                    targetInstanceCount: request.RequestedTargetInstanceCount,
                    createdInstanceCount: 0)
            };
        }

        /// <summary>
        /// Creates provider result metadata for local scale-out operations.
        /// </summary>
        /// <param name="request">The provider scale-out request.</param>
        /// <param name="status">The provider operation status.</param>
        /// <param name="activeInstanceCount">The active runtime instance count within the requested scope.</param>
        /// <param name="targetInstanceCount">The target runtime instance count within the requested scope.</param>
        /// <param name="createdInstanceCount">The number of runtime instances created by this operation.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiRuntimeScaleOutProviderRequest request,
            string status,
            int activeInstanceCount,
            int targetInstanceCount,
            int createdInstanceCount)
        {
            var metadata =
                new Dictionary<string, string>(
                    request.Metadata ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = ProviderName,
                    ["provider"] = ProviderName,
                    ["providerStatus"] = status,
                    ["scaleOutRequestId"] = request.RequestId,
                    ["sharedRunId"] = request.SharedRunId,
                    ["controlPlaneId"] = request.ControlPlaneId,
                    ["activeInstanceCount"] = activeInstanceCount.ToString(CultureInfo.InvariantCulture),
                    ["targetInstanceCount"] = targetInstanceCount.ToString(CultureInfo.InvariantCulture),
                    ["createdInstanceCount"] = createdInstanceCount.ToString(CultureInfo.InvariantCulture)
                };

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
                metadata["tenant.groupId"] = request.TenantGroupId;
            }

            if (HasExplicitTenantRuntimeSettings(request))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] =
                    request.IsolationMode.ToString();

                metadata[AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] =
                    request.PreferDedicatedCapacity.ToString();

                metadata[AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] =
                    request.AllowSharedFallback.ToString();

                if (request.MaxRuntimeInstances.HasValue)
                {
                    metadata["runtime.maxRuntimeInstances"] =
                        request.MaxRuntimeInstances.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrWhiteSpace(request.RuntimeInstanceIdPrefix))
                {
                    metadata["runtime.instanceIdPrefix"] =
                        request.RuntimeInstanceIdPrefix;
                }

                if (request.WorkerCountPerInstance.HasValue)
                {
                    metadata["runtime.workerCountPerInstance"] =
                        request.WorkerCountPerInstance.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (request.MaxConcurrentRunsPerInstance.HasValue)
                {
                    metadata["runtime.maxConcurrentRunsPerInstance"] =
                        request.MaxConcurrentRunsPerInstance.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (request.LocalQueueCapacity.HasValue)
                {
                    metadata["runtime.localQueueCapacity"] =
                        request.LocalQueueCapacity.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            return metadata;
        }

        /// <summary>
        /// Gets the configured MCP host mode as a raw configuration value.
        /// </summary>
        /// <returns>The configured host mode, or an unknown marker.</returns>
        private string GetHostMode()
        {
            return this.configuration["AiMcpHost:Mode"]
                ?? UnknownHostMode;
        }

        /// <summary>
        /// Gets the configured shared queue pump flag as a raw configuration value.
        /// </summary>
        /// <returns>The configured shared queue pump flag, or an unknown marker.</returns>
        private string GetEnableSharedQueuePump()
        {
            return this.configuration["AiMcpHost:EnableSharedQueuePump"]
                ?? UnknownEnableSharedQueuePump;
        }

        /// <summary>
        /// Creates a globally scoped runtime instance identifier for a local runtime instance.
        /// </summary>
        /// <param name="hostId">The runtime host identifier.</param>
        /// <param name="runtimeId">The runtime identifier inside the host.</param>
        /// <returns>The globally scoped runtime instance identifier.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when host id or runtime id is missing.
        /// </exception>
        private static string CreateRuntimeInstanceId(
            string hostId,
            string runtimeId)
        {
            if (string.IsNullOrWhiteSpace(hostId))
            {
                throw new InvalidOperationException(
                    "Runtime host identity HostId must be provided.");
            }

            if (string.IsNullOrWhiteSpace(runtimeId))
            {
                throw new InvalidOperationException(
                    "RuntimeId must be provided.");
            }

            return $"{hostId}:{runtimeId}";
        }

        /// <summary>
        /// Validates global local runtime instance pool options used as fallback values.
        /// </summary>
        private void ValidateOptions()
        {
            if (this.options.InstanceCount <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool InstanceCount must be greater than zero.");
            }

            if (this.options.WorkerCountPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool WorkerCountPerInstance must be greater than zero.");
            }

            if (this.options.MaxConcurrentRunsPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool MaxConcurrentRunsPerInstance must be greater than zero.");
            }

            if (this.options.LocalQueueCapacity.HasValue &&
                this.options.LocalQueueCapacity.Value < 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool LocalQueueCapacity must be null, zero, or greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeInstanceIdPrefix))
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool RuntimeInstanceIdPrefix must be provided.");
            }
        }

        /// <summary>
        /// Validates resolved scale-out settings before creating runtime instances.
        /// </summary>
        /// <param name="settings">The resolved scale-out settings.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when one of the resolved settings is invalid.
        /// </exception>
        private static void ValidateScaleOutSettings(
            LocalRuntimeInstanceScaleOutSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.RuntimeInstanceIdPrefix))
            {
                throw new InvalidOperationException(
                    "Local runtime instance scale-out RuntimeInstanceIdPrefix must be provided.");
            }

            if (settings.WorkerCountPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance scale-out WorkerCountPerInstance must be greater than zero.");
            }

            if (settings.MaxConcurrentRunsPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance scale-out MaxConcurrentRunsPerInstance must be greater than zero.");
            }

            if (settings.LocalQueueCapacity.HasValue &&
                settings.LocalQueueCapacity.Value < 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance scale-out LocalQueueCapacity must be null, zero, or greater than zero.");
            }
        }

        /// <summary>
        /// Represents resolved local runtime instance scale-out settings.
        /// </summary>
        /// <param name="RuntimeInstanceIdPrefix">The runtime instance id prefix.</param>
        /// <param name="WorkerCountPerInstance">The worker count per instance.</param>
        /// <param name="MaxConcurrentRunsPerInstance">The maximum concurrent run count per instance.</param>
        /// <param name="LocalQueueCapacity">The local queue capacity.</param>
        /// <param name="Metadata">The metadata copied to the created runtime instance host.</param>
        private sealed record LocalRuntimeInstanceScaleOutSettings(
            string RuntimeInstanceIdPrefix,
            int WorkerCountPerInstance,
            int MaxConcurrentRunsPerInstance,
            int? LocalQueueCapacity,
            IReadOnlyDictionary<string, string> Metadata);
    }
}