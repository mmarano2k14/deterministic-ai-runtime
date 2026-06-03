using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// - This service does not replace Kubernetes scaling; it provides the local
    ///   equivalent for tests, demos, and development.
    /// </remarks>
    public sealed class AiLocalRuntimeInstancePoolHostedService :
        IHostedService,
        IAsyncDisposable
    {
        private readonly IAiLocalRuntimeInstanceHostFactory hostFactory;
        private readonly IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry;
        private readonly AiLocalRuntimeInstancePoolOptions options;
        private readonly ILogger<AiLocalRuntimeInstancePoolHostedService> logger;

        private readonly List<IAiLocalRuntimeInstanceHost> hosts = new();

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiLocalRuntimeInstancePoolHostedService"/> class.
        /// </summary>
        public AiLocalRuntimeInstancePoolHostedService(
            IAiLocalRuntimeInstanceHostFactory hostFactory,
            IAiSharedRuntimeInstanceRegistry sharedRuntimeInstanceRegistry,
            IOptions<AiLocalRuntimeInstancePoolOptions> options,
            ILogger<AiLocalRuntimeInstancePoolHostedService> logger)
        {
            this.hostFactory = hostFactory
                ?? throw new ArgumentNullException(nameof(hostFactory));

            this.sharedRuntimeInstanceRegistry = sharedRuntimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(sharedRuntimeInstanceRegistry));

            this.options = options?.Value
                ?? throw new ArgumentNullException(nameof(options));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            if (!options.Enabled)
            {
                logger.LogInformation(
                    "Local runtime instance pool is disabled.");

                return;
            }

            ValidateOptions();

            logger.LogInformation(
                "Starting local runtime instance pool. InstanceCount={InstanceCount}, WorkerCountPerInstance={WorkerCountPerInstance}, MaxConcurrentRunsPerInstance={MaxConcurrentRunsPerInstance}, LocalQueueCapacity={LocalQueueCapacity}",
                options.InstanceCount,
                options.WorkerCountPerInstance,
                options.MaxConcurrentRunsPerInstance,
                options.LocalQueueCapacity?.ToString() ?? "unlimited");

            for (var index = 1; index <= options.InstanceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var runtimeInstanceId =
                    $"{options.RuntimeInstanceIdPrefix}-{index}";

                var host =
                    await hostFactory
                        .CreateAsync(
                            runtimeInstanceId,
                            options.WorkerCountPerInstance,
                            options.MaxConcurrentRunsPerInstance,
                            options.LocalQueueCapacity,
                            cancellationToken)
                        .ConfigureAwait(false);

                await sharedRuntimeInstanceRegistry
                    .RegisterAsync(
                        host.SharedRuntimeInstance,
                        cancellationToken)
                    .ConfigureAwait(false);

                await host
                    .StartAsync(cancellationToken)
                    .ConfigureAwait(false);

                hosts.Add(host);

                logger.LogInformation(
                    "Local runtime instance started. RuntimeInstanceId={RuntimeInstanceId}, WorkerCount={WorkerCount}",
                    host.RuntimeInstanceId,
                    host.WorkerCount);
            }

            logger.LogInformation(
                "Local runtime instance pool started. ActiveInstanceCount={ActiveInstanceCount}",
                hosts.Count);
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken)
        {
            if (hosts.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "Stopping local runtime instance pool. ActiveInstanceCount={ActiveInstanceCount}",
                hosts.Count);

            foreach (var host in hosts.AsEnumerable().Reverse())
            {
                await sharedRuntimeInstanceRegistry
                    .UnregisterAsync(
                        host.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

                await host
                    .StopAsync(cancellationToken)
                    .ConfigureAwait(false);

                logger.LogInformation(
                    "Local runtime instance stopped. RuntimeInstanceId={RuntimeInstanceId}",
                    host.RuntimeInstanceId);
            }

            logger.LogInformation(
                "Local runtime instance pool stopped.");
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            foreach (var host in hosts.AsEnumerable().Reverse())
            {
                await host
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            hosts.Clear();
        }

        /// <summary>
        /// Validates local runtime instance pool options.
        /// </summary>
        private void ValidateOptions()
        {
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
        }
    }
}