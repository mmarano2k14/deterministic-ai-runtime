using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

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
        private readonly AiLocalRuntimeInstancePoolOptions options;
        private readonly IConfiguration configuration;
        private readonly ILogger<AiLocalRuntimeInstancePoolHostedService> logger;

        private int stopRequested;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstancePoolHostedService"/> class.
        /// </summary>
        /// <param name="scaler">The local runtime instance scaler.</param>
        /// <param name="runtimeHostIdentity">The runtime host identity.</param>
        /// <param name="options">The local runtime instance pool options.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="logger">The logger.</param>
        public AiLocalRuntimeInstancePoolHostedService(
            IAiLocalRuntimeInstanceScaler scaler,
            IAiRuntimeHostIdentity runtimeHostIdentity,
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

            this.logger.LogInformation(
                "Evaluating local runtime instance pool startup. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.options.Enabled,
                this.options.InstanceCount,
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.RuntimeInstanceIdPrefix);

            if (!this.options.Enabled)
            {
                this.logger.LogInformation(
                    "Local runtime instance pool skipped because it is disabled by options. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    this.options.Enabled,
                    this.options.RuntimeInstanceIdPrefix);

                return;
            }

            ValidateOptions(
                hostMode,
                enableSharedQueuePump);

            this.logger.LogInformation(
                "Starting local runtime instance pool through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, TargetInstanceCount={TargetInstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.options.Enabled,
                this.options.InstanceCount,
                this.options.WorkerCountPerInstance,
                this.options.MaxConcurrentRunsPerInstance,
                this.options.LocalQueueCapacity?.ToString() ?? "unlimited",
                this.options.RuntimeInstanceIdPrefix);

            var result =
                await this.scaler
                    .EnsureCapacityAsync(
                        new AiRuntimeScaleOutProviderRequest
                        {
                            RequestId = $"local-pool-startup-{Guid.NewGuid():N}",
                            ControlPlaneId = this.runtimeHostIdentity.HostId,
                            SharedRunId = "local-pool-startup",
                            CurrentInstanceCount = 0,
                            RequestedTargetInstanceCount = this.options.InstanceCount,
                            MaxInstanceCount = this.options.InstanceCount,
                            ProviderHint = "local",
                            RequestedBy = "local-runtime-instance-pool",
                            Source = nameof(AiLocalRuntimeInstancePoolHostedService),
                            Reason = "Initial local runtime instance pool startup.",
                            Metadata = new Dictionary<string, string>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["hostMode"] = hostMode,
                                ["enableSharedQueuePump"] = enableSharedQueuePump,
                                ["runtimeHostId"] = this.runtimeHostIdentity.HostId,
                                ["runtimeInstanceIdPrefix"] = this.options.RuntimeInstanceIdPrefix
                            }
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
                "Local runtime instance pool started through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
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

            if (Interlocked.Exchange(ref this.stopRequested, 1) == 1)
            {
                this.logger.LogInformation(
                    "Local runtime instance pool stop skipped because stop is already requested. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    this.runtimeHostIdentity.HostId,
                    this.scaler.ActiveInstanceCount,
                    this.options.RuntimeInstanceIdPrefix);

                return;
            }

            this.logger.LogInformation(
                "Stopping local runtime instance pool through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.scaler.ActiveInstanceCount,
                this.options.RuntimeInstanceIdPrefix);

            await this.scaler
                .StopAsync(
                    cancellationToken)
                .ConfigureAwait(false);

            this.logger.LogInformation(
                "Local runtime instance pool stopped through scaler. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.scaler.ActiveInstanceCount,
                this.options.RuntimeInstanceIdPrefix);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            this.logger.LogInformation(
                "Disposing local runtime instance pool hosted service. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, StopRequested={StopRequested}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.scaler.ActiveInstanceCount,
                this.stopRequested,
                this.options.RuntimeInstanceIdPrefix);

            this.logger.LogInformation(
                "Local runtime instance pool hosted service disposed. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                this.runtimeHostIdentity.HostId,
                this.options.RuntimeInstanceIdPrefix);

            return ValueTask.CompletedTask;
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