using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;

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

        private readonly IAiLocalRuntimeInstanceHostFactory hostFactory;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly IAiRuntimeHostIdentity runtimeHostIdentity;
        private readonly AiLocalRuntimeInstancePoolOptions options;
        private readonly IConfiguration configuration;
        private readonly ILogger<AiLocalRuntimeInstancePoolHostedService> logger;

        private readonly List<IAiLocalRuntimeInstanceHost> hosts = new();
        private int stopRequested;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstancePoolHostedService"/> class.
        /// </summary>
        public AiLocalRuntimeInstancePoolHostedService(
            IAiLocalRuntimeInstanceHostFactory hostFactory,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IAiRuntimeHostIdentity runtimeHostIdentity,
            IOptions<AiLocalRuntimeInstancePoolOptions> options,
            IConfiguration configuration,
            ILogger<AiLocalRuntimeInstancePoolHostedService> logger)
        {
            this.hostFactory = hostFactory
                ?? throw new ArgumentNullException(nameof(hostFactory));

            this.sharedRuntimeInstanceRegistry = sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.runtimeHostIdentity = runtimeHostIdentity
                ?? throw new ArgumentNullException(nameof(runtimeHostIdentity));

            this.options = options?.Value
                ?? throw new ArgumentNullException(nameof(options));

            this.configuration = configuration
                ?? throw new ArgumentNullException(nameof(configuration));

            this.logger = logger
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

            logger.LogInformation(
                "Evaluating local runtime instance pool startup. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.Enabled,
                options.InstanceCount,
                options.WorkerCountPerInstance,
                options.MaxConcurrentRunsPerInstance,
                options.LocalQueueCapacity?.ToString() ?? "unlimited",
                options.RuntimeInstanceIdPrefix);

            if (!options.Enabled)
            {
                logger.LogInformation(
                    "Local runtime instance pool skipped because it is disabled by options. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    options.Enabled,
                    options.RuntimeInstanceIdPrefix);

                return;
            }

            ValidateOptions(
                hostMode,
                enableSharedQueuePump);

            logger.LogInformation(
                "Starting local runtime instance pool. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.Enabled,
                options.InstanceCount,
                options.WorkerCountPerInstance,
                options.MaxConcurrentRunsPerInstance,
                options.LocalQueueCapacity?.ToString() ?? "unlimited",
                options.RuntimeInstanceIdPrefix);

            for (var index = 1; index <= options.InstanceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runtimeId =
                    $"{options.RuntimeInstanceIdPrefix}-{index}";

                var runtimeInstanceId =
                    CreateRuntimeInstanceId(
                        runtimeHostIdentity.HostId,
                        runtimeId);

                logger.LogInformation(
                    "Creating local runtime instance host. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, Index={Index}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    runtimeId,
                    runtimeInstanceId,
                    index,
                    options.InstanceCount,
                    options.WorkerCountPerInstance,
                    options.MaxConcurrentRunsPerInstance,
                    options.LocalQueueCapacity?.ToString() ?? "unlimited");

                var host =
                    await hostFactory
                        .CreateAsync(
                            runtimeInstanceId,
                            options.WorkerCountPerInstance,
                            options.MaxConcurrentRunsPerInstance,
                            options.LocalQueueCapacity,
                            cancellationToken)
                        .ConfigureAwait(false);

                logger.LogInformation(
                    "Registering local runtime instance in shared runtime registry. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    runtimeId,
                    host.RuntimeInstanceId,
                    host.WorkerCount);

                await sharedRuntimeInstanceRegistry
                    .RegisterAsync(
                        host.SharedRuntimeInstance,
                        cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Starting local runtime instance host. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    runtimeId,
                    host.RuntimeInstanceId,
                    host.WorkerCount);

                await host
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);

                hosts.Add(host);

                logger.LogInformation(
                    "Local runtime instance started. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeId={RuntimeId}, RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}, ActiveInstanceCount={ActiveInstanceCount}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    runtimeId,
                    host.RuntimeInstanceId,
                    host.WorkerCount,
                    hosts.Count);
            }

            logger.LogInformation(
                "Local runtime instance pool started. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                hosts.Count,
                options.RuntimeInstanceIdPrefix);
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken)
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            if (Interlocked.Exchange(ref stopRequested, 1) == 1)
            {
                logger.LogInformation(
                    "Local runtime instance pool stop skipped because stop is already requested. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    hosts.Count,
                    options.RuntimeInstanceIdPrefix);

                return;
            }

            logger.LogInformation(
                "Evaluating local runtime instance pool stop. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, Enabled={Enabled}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                hosts.Count,
                options.Enabled,
                options.RuntimeInstanceIdPrefix);

            if (hosts.Count == 0)
            {
                logger.LogInformation(
                    "Local runtime instance pool stop completed without active hosts. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    options.RuntimeInstanceIdPrefix);

                return;
            }

            logger.LogInformation(
                "Stopping local runtime instance pool. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                hosts.Count,
                options.RuntimeInstanceIdPrefix);

            foreach (var host in hosts.AsEnumerable().Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();

                logger.LogInformation(
                    "Stopping local runtime instance. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    host.RuntimeInstanceId);

                await host
                    .StopAsync(cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Unregistering local runtime instance from shared runtime registry. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    host.RuntimeInstanceId);

                await sharedRuntimeInstanceRegistry
                    .UnregisterAsync(
                        host.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Local runtime instance stopped and unregistered from shared runtime registry. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    host.RuntimeInstanceId);
            }

            logger.LogInformation(
                "Local runtime instance pool stopped. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.RuntimeInstanceIdPrefix);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var hostMode =
                GetHostMode();

            var enableSharedQueuePump =
                GetEnableSharedQueuePump();

            logger.LogInformation(
                "Disposing local runtime instance pool hosted service. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, ActiveInstanceCount={ActiveInstanceCount}, StopRequested={StopRequested}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                hosts.Count,
                stopRequested,
                options.RuntimeInstanceIdPrefix);

            foreach (var host in hosts.AsEnumerable().Reverse())
            {
                logger.LogInformation(
                    "Disposing local runtime instance host. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceId={RuntimeInstanceId}",
                    hostMode,
                    enableSharedQueuePump,
                    runtimeHostIdentity.HostId,
                    host.RuntimeInstanceId);

                await host
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            hosts.Clear();

            logger.LogInformation(
                "Local runtime instance pool hosted service disposed. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.RuntimeInstanceIdPrefix);
        }

        /// <summary>
        /// Gets the configured MCP host mode as a raw configuration value.
        /// </summary>
        /// <returns>The configured host mode, or an unknown marker.</returns>
        private string GetHostMode()
        {
            return configuration["AiMcpHost:Mode"]
                ?? UnknownHostMode;
        }

        /// <summary>
        /// Gets the configured shared queue pump flag as a raw configuration value.
        /// </summary>
        /// <returns>The configured shared queue pump flag, or an unknown marker.</returns>
        private string GetEnableSharedQueuePump()
        {
            return configuration["AiMcpHost:EnableSharedQueuePump"]
                ?? UnknownEnableSharedQueuePump;
        }

        /// <summary>
        /// Creates the globally unique runtime instance id for the current host.
        /// </summary>
        /// <param name="hostId">The current runtime host id.</param>
        /// <param name="runtimeId">The logical runtime id inside the host.</param>
        /// <returns>The globally unique runtime instance id.</returns>
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
        /// Validates local runtime instance pool options.
        /// </summary>
        /// <param name="hostMode">The configured MCP host mode.</param>
        /// <param name="enableSharedQueuePump">The configured shared queue pump flag.</param>
        private void ValidateOptions(
            string hostMode,
            string enableSharedQueuePump)
        {
            logger.LogInformation(
                "Validating local runtime instance pool options. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, Enabled={Enabled}, InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.Enabled,
                options.InstanceCount,
                options.WorkerCountPerInstance,
                options.MaxConcurrentRunsPerInstance,
                options.LocalQueueCapacity?.ToString() ?? "unlimited",
                options.RuntimeInstanceIdPrefix);

            if (options.InstanceCount <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool InstanceCount must be greater than zero.");
            }

            if (options.WorkerCountPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool WorkerCountPerInstance must be greater than zero.");
            }

            if (options.MaxConcurrentRunsPerInstance <= 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool MaxConcurrentRunsPerInstance must be greater than zero.");
            }

            if (options.LocalQueueCapacity.HasValue &&
                options.LocalQueueCapacity.Value < 0)
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool LocalQueueCapacity must be null, zero, or greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(options.RuntimeInstanceIdPrefix))
            {
                throw new InvalidOperationException(
                    "Local runtime instance pool RuntimeInstanceIdPrefix must be provided.");
            }

            logger.LogInformation(
                "Local runtime instance pool options validated. HostMode={HostMode}, EnableSharedQueuePump={EnableSharedQueuePump}, HostId={HostId}, RuntimeInstanceIdPrefix={RuntimeInstanceIdPrefix}",
                hostMode,
                enableSharedQueuePump,
                runtimeHostIdentity.HostId,
                options.RuntimeInstanceIdPrefix);
        }
    }
}