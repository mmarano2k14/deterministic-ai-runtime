using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

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

                if (targetInstanceCount <= this.hosts.Count)
                {
                    return new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        Rejected = false,
                        RuntimeInstanceId = this.hosts.LastOrDefault()?.RuntimeInstanceId,
                        ProviderOperationId = $"local-scaleout-noop-{request.RequestId}",
                        Message = $"Local runtime instance capacity already satisfies target '{targetInstanceCount}'.",
                        Metadata = CreateMetadata(
                            request,
                            "noop",
                            this.hosts.Count,
                            targetInstanceCount,
                            createdInstanceCount: 0)
                    };
                }

                var createdInstanceCount = 0;
                IAiLocalRuntimeInstanceHost? lastCreatedHost = null;

                while (this.hosts.Count < targetInstanceCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var index =
                        this.hosts.Count + 1;

                    var host =
                        await this.CreateStartAndRegisterHostAsync(
                                index,
                                targetInstanceCount,
                                cancellationToken)
                            .ConfigureAwait(false);

                    this.hosts.Add(host);
                    lastCreatedHost = host;
                    createdInstanceCount++;
                }

                var completedAtUtc =
                    DateTimeOffset.UtcNow;

                this.logger.LogInformation(
                    "Local runtime instance scale-out fulfilled. HostId={HostId}, RequestId={RequestId}, SharedRunId={SharedRunId}, ActiveInstanceCount={ActiveInstanceCount}, TargetInstanceCount={TargetInstanceCount}, CreatedInstanceCount={CreatedInstanceCount}, DurationMs={DurationMs}",
                    this.runtimeHostIdentity.HostId,
                    request.RequestId,
                    request.SharedRunId,
                    this.hosts.Count,
                    targetInstanceCount,
                    createdInstanceCount,
                    Math.Max(0, (long)(completedAtUtc - startedAtUtc).TotalMilliseconds));

                return new AiRuntimeScaleOutProviderResult
                {
                    Success = true,
                    Rejected = false,
                    RuntimeInstanceId = lastCreatedHost?.RuntimeInstanceId ?? this.hosts.LastOrDefault()?.RuntimeInstanceId,
                    ProviderOperationId = $"local-scaleout-{request.RequestId}",
                    Message = $"Local runtime instance scale-out fulfilled. Created {createdInstanceCount} runtime instance(s).",
                    Metadata = CreateMetadata(
                        request,
                        "fulfilled",
                        this.hosts.Count,
                        targetInstanceCount,
                        createdInstanceCount)
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogWarning(
                    exception,
                    "Local runtime instance scale-out failed. HostId={HostId}, RequestId={RequestId}, SharedRunId={SharedRunId}, ActiveInstanceCount={ActiveInstanceCount}",
                    this.runtimeHostIdentity.HostId,
                    request.RequestId,
                    request.SharedRunId,
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

        private async Task<IAiLocalRuntimeInstanceHost> CreateStartAndRegisterHostAsync(
            int index,
            int targetInstanceCount,
            CancellationToken cancellationToken)
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            var runtimeId =
                $"{this.options.RuntimeInstanceIdPrefix}-{index}";

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
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.Metadata.Count);

            var host =
                await this.hostFactory
                    .CreateAsync(
                        runtimeInstanceId,
                        this.options.WorkerCountPerInstance,
                        this.options.MaxConcurrentRunsPerInstance,
                        this.options.LocalQueueCapacity,
                        this.options.Metadata,
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

        private int ResolveTargetInstanceCount(
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
                this.hosts.Count);
        }

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
                    ["activeInstanceCount"] = activeInstanceCount.ToString(),
                    ["targetInstanceCount"] = targetInstanceCount.ToString(),
                    ["createdInstanceCount"] = createdInstanceCount.ToString()
                };

            return metadata;
        }

        private string GetHostMode()
        {
            return this.configuration["AiMcpHost:Mode"]
                ?? UnknownHostMode;
        }

        private string GetEnableSharedQueuePump()
        {
            return this.configuration["AiMcpHost:EnableSharedQueuePump"]
                ?? UnknownEnableSharedQueuePump;
        }

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
    }
}