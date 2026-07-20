using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Starts and stops a pool of local runtime instances inside the current process.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Allows one MCP/control-plane host to simulate multiple runtime instances.
    /// - Reuses the same logical model as Kubernetes, where each runtime host
    ///   exposes one or more runtime instances.
    /// - Enables local benchmarks with multiple runtime instances and workers
    ///   before deploying to multiple pods.
    ///
    /// IMPORTANT:
    /// - Each runtime instance must own an isolated local runtime controller.
    /// - Each runtime instance must expose a shared runtime instance endpoint.
    /// - Runtime instance ids are globally scoped with the current host id:
    ///   {HostId}:{RuntimeId}.
    /// - This prevents Redis-backed registries from mixing runtime instances
    ///   created by different hosts, fixtures, pods, or test processes.
    /// - This service does not replace Kubernetes scaling; it provides the local
    ///   equivalent for tests, demos, and development.
    /// </remarks>
    public sealed class AiLocalRuntimeInstancePoolHostedService :
        IHostedService,
        IAsyncDisposable
    {
        private const string UnknownHostMode = "(unknown)";
        private const string UnknownEnableSharedQueuePump = "(unknown)";

        private readonly IAiLocalRuntimeInstanceScaler scaler;
        private readonly IAiRuntimeHostIdentity runtimeHostIdentity;
        private readonly IAiControlPlaneIdResolver controlPlaneIdResolver;
        private readonly AiLocalRuntimeInstancePoolOptions options;
        private readonly IConfiguration configuration;
        private readonly ILogger<AiLocalRuntimeInstancePoolHostedService> logger;

        private int stopRequested;
        private string? resolvedControlPlaneId;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstancePoolHostedService"/> class.
        /// </summary>
        /// <param name="scaler">The local runtime instance scaler.</param>
        /// <param name="runtimeHostIdentity">The runtime host identity.</param>
        /// <param name="controlPlaneIdResolver">The logical control-plane id resolver.</param>
        /// <param name="options">The local runtime instance pool options.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="logger">The logger.</param>
        public AiLocalRuntimeInstancePoolHostedService(
            IAiLocalRuntimeInstanceScaler scaler,
            IAiRuntimeHostIdentity runtimeHostIdentity,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IOptions<AiLocalRuntimeInstancePoolOptions> options,
            IConfiguration configuration,
            ILogger<AiLocalRuntimeInstancePoolHostedService> logger)
        {
            this.scaler =
                scaler
                ?? throw new ArgumentNullException(nameof(scaler));

            this.runtimeHostIdentity =
                runtimeHostIdentity
                ?? throw new ArgumentNullException(nameof(runtimeHostIdentity));

            this.controlPlaneIdResolver =
                controlPlaneIdResolver
                ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));

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
        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "Evaluating local runtime instance pool startup. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.options.Enabled,
                this.options.InstanceCount,
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.RuntimeInstanceIdPrefix);

            if (!this.options.Enabled)
            {
                this.logger.LogInformation(
                    "Local runtime instance pool skipped because it is disabled by options. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, Enabled={Enabled}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    controlPlaneId,
                    this.options.Enabled,
                    this.options.RuntimeInstanceIdPrefix);

                return;
            }

            ValidateOptions(
                hostMode,
                enableSharedQueuePump);

            this.logger.LogInformation(
                "Starting local runtime instance pool through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, Enabled={Enabled}, TargetInstanceCount={TargetInstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.options.Enabled,
                this.options.InstanceCount,
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.RuntimeInstanceIdPrefix);

            var metadata =
                await this.CreateScaleOutMetadataAsync(
                        hostMode,
                        enableSharedQueuePump,
                        cancellationToken)
                    .ConfigureAwait(false);

            var result =
                await this.scaler
                    .EnsureCapacityAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = $"local-pool-startup-{Guid.NewGuid():N}",
                            ControlPlaneId = controlPlaneId,
                            ExecutionContextSnapshot = CreateLocalPoolStartupExecutionContextSnapshot(
                                controlPlaneId),
                            SharedRunId = "local-pool-startup",
                            CurrentInstanceCount = 0,
                            RequestedTargetInstanceCount = this.options.InstanceCount,
                            MaxInstanceCount = this.options.InstanceCount,
                            ProviderHint = "local",
                            RequestedBy = "local-runtime-instance-pool",
                            Source = nameof(AiLocalRuntimeInstancePoolHostedService),
                            Reason = "Initial local runtime instance pool startup.",
                            Metadata = metadata
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    result.Message ??
                    result.FailureReason ??
                    "Local runtime instance pool startup failed.");
            }

            this.logger.LogInformation(
                "Local runtime instance pool started through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.scaler.ActiveInstanceCount,
                this.options.RuntimeInstanceIdPrefix);
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken)
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(cancellationToken)
                    .ConfigureAwait(false);

            if (Interlocked.Exchange(ref this.stopRequested, 1) == 1)
            {
                this.logger.LogInformation(
                    "Local runtime instance pool stop skipped because stop is already requested. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    controlPlaneId,
                    this.scaler.ActiveInstanceCount,
                    this.options.RuntimeInstanceIdPrefix);

                return;
            }

            this.logger.LogInformation(
                "Stopping local runtime instance pool through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.scaler.ActiveInstanceCount,
                this.options.RuntimeInstanceIdPrefix);

            await this.scaler
                .StopAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "Local runtime instance pool stopped through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.scaler.ActiveInstanceCount,
                this.options.RuntimeInstanceIdPrefix);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            var controlPlaneId =
                await this.ResolveControlPlaneIdAsync(CancellationToken.None)
                    .ConfigureAwait(false);

            this.logger.LogInformation(
                "Disposing local runtime instance pool hosted service. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, ActiveInstanceCount={ActiveInstanceCount}, StopRequested={StopRequested}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.scaler.ActiveInstanceCount,
                this.stopRequested,
                this.options.RuntimeInstanceIdPrefix);

            this.logger.LogInformation(
                "Local runtime instance pool hosted service disposed. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ControlPlaneId={ControlPlaneId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                controlPlaneId,
                this.options.RuntimeInstanceIdPrefix);
        }

        /// <summary>
        /// Resolves the logical control-plane identifier used by local runtime instance pool startup.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved logical control-plane identifier.</returns>
        private async Task<string> ResolveControlPlaneIdAsync(
            CancellationToken cancellationToken)
        {
            var cachedControlPlaneId =
                Volatile.Read(ref this.resolvedControlPlaneId);

            if (!string.IsNullOrWhiteSpace(cachedControlPlaneId))
            {
                return cachedControlPlaneId;
            }

            var controlPlaneId =
                await this.controlPlaneIdResolver
                    .ResolveAsync(
                        new AiControlPlaneIdResolutionRequest
                        {
                            Source = "local-runtime-instance-pool-hosted-service",
                            AllowGeneratedFallback = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            Volatile.Write(
                ref this.resolvedControlPlaneId,
                controlPlaneId);

            return controlPlaneId;
        }

        /// <summary>
        /// Creates metadata for the local runtime instance pool scale-out request.
        /// </summary>
        /// <param name="hostMode">The configured MCP host mode.</param>
        /// <param name="enableSharedQueuePump">The configured shared queue pump flag.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scale-out metadata.</returns>
        private async Task<IReadOnlyDictionary<string, string>> CreateScaleOutMetadataAsync(
            string hostMode,
            string enableSharedQueuePump,
            CancellationToken cancellationToken)
        {
            var metadata =
                new Dictionary<string, string>(
                    await this.controlPlaneIdResolver
                        .ResolveMetadataAsync(
                            new AiControlPlaneIdResolutionRequest
                            {
                                Source = "local-runtime-instance-pool-hosted-service",
                                AllowGeneratedFallback = false
                            },
                            cancellationToken)
                        .ConfigureAwait(false),
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["hostMode"] = hostMode,
                    ["enableSharedQueuePump"] = enableSharedQueuePump,
                    ["runtimeHostId"] = this.runtimeHostIdentity.HostId,
                    ["runtimeInstanceIdPrefix"] = this.options.RuntimeInstanceIdPrefix
                };

            return metadata;
        }

        /// <summary>
        /// Creates the execution context snapshot used for local runtime instance pool startup.
        /// </summary>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <returns>The local runtime instance pool startup execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateLocalPoolStartupExecutionContextSnapshot(
            string controlPlaneId)
        {
            var safeControlPlaneId =
                string.IsNullOrWhiteSpace(controlPlaneId)
                    ? "unknown-control-plane"
                    : controlPlaneId;

            return new ExecutionContextSnapshot
            {
                ContextKey = $"system:local-runtime-instance-pool:{safeControlPlaneId}",
                Project = "system",
                UserId = "system",
                TenantId = "system",
                TenantGroupId = "system",
                CurrentNamespace = "system",
                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = "system",
                        Trns = new HashSet<string>()
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 0,
                CreatedAtUtc = DateTime.UtcNow
            };
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
        /// Validates local runtime instance pool options.
        /// </summary>
        /// <param name="hostMode">The configured MCP host mode.</param>
        /// <param name="enableSharedQueuePump">The configured shared queue pump flag.</param>
        private void ValidateOptions(
            string hostMode,
            string enableSharedQueuePump)
        {
            this.logger.LogInformation(
                "Validating local runtime instance pool options. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.options.Enabled,
                this.options.InstanceCount,
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.RuntimeInstanceIdPrefix);

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

            this.logger.LogInformation(
                "Local runtime instance pool options validated. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.options.RuntimeInstanceIdPrefix);
        }
    }
}