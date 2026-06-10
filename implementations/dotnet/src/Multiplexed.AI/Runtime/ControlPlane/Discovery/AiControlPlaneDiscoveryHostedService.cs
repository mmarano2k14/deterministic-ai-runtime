using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.AI.Configuration;

namespace Multiplexed.AI.Runtime.ControlPlane.Discovery
{
    /// <summary>
    /// Publishes and refreshes the control-plane discovery descriptor for hosts
    /// that own the active control-plane identity.
    /// </summary>
    public sealed class AiControlPlaneDiscoveryHostedService : BackgroundService
    {
        private readonly IAiControlPlaneDiscoveryStore discoveryStore;
        private readonly IAiControlPlaneHostIdentity controlPlaneHostIdentity;
        private readonly IOptions<AiEngineOptions> engineOptions;
        private readonly ILogger<AiControlPlaneDiscoveryHostedService> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiControlPlaneDiscoveryHostedService"/> class.
        /// </summary>
        /// <param name="discoveryStore">The control-plane discovery store.</param>
        /// <param name="controlPlaneHostIdentity">The control-plane host identity.</param>
        /// <param name="engineOptions">The AI engine options.</param>
        /// <param name="logger">The logger.</param>
        public AiControlPlaneDiscoveryHostedService(
            IAiControlPlaneDiscoveryStore discoveryStore,
            IAiControlPlaneHostIdentity controlPlaneHostIdentity,
            IOptions<AiEngineOptions> engineOptions,
            ILogger<AiControlPlaneDiscoveryHostedService> logger)
        {
            ArgumentNullException.ThrowIfNull(discoveryStore);
            ArgumentNullException.ThrowIfNull(controlPlaneHostIdentity);
            ArgumentNullException.ThrowIfNull(engineOptions);
            ArgumentNullException.ThrowIfNull(logger);

            this.discoveryStore = discoveryStore;
            this.controlPlaneHostIdentity = controlPlaneHostIdentity;
            this.engineOptions = engineOptions;
            this.logger = logger;
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var options = engineOptions.Value.ControlPlane;

            if (!options.EnableDiscovery)
            {
                logger.LogInformation(
                    "Control-plane discovery publisher disabled because discovery is disabled.");

                return;
            }

            if (!options.PublishDiscovery)
            {
                logger.LogInformation(
                    "Control-plane discovery publisher disabled because this host is not configured as publisher.");

                return;
            }

            if (string.IsNullOrWhiteSpace(options.RedisDiscoveryKey))
            {
                throw new InvalidOperationException(
                    "A Redis discovery key is required when publishing control-plane discovery.");
            }

            var ttl =
                options.EnableDiscoveryTtl
                    ? options.DiscoveryTtl
                    : (TimeSpan?)null;

            var refreshInterval = ResolveRefreshInterval(options);

            logger.LogInformation(
                "Control-plane discovery publisher started. RedisDiscoveryKey={RedisDiscoveryKey}, ControlPlaneId={ControlPlaneId}, EnableTtl={EnableTtl}, Ttl={Ttl}, RefreshInterval={RefreshInterval}",
                options.RedisDiscoveryKey,
                controlPlaneHostIdentity.ControlPlaneHostId,
                options.EnableDiscoveryTtl,
                ttl,
                refreshInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var existingDescriptor =
                        await discoveryStore
                            .GetAsync(
                                options.RedisDiscoveryKey,
                                stoppingToken)
                            .ConfigureAwait(false);

                    if (existingDescriptor is not null &&
                        !string.IsNullOrWhiteSpace(existingDescriptor.ControlPlaneId) &&
                        !string.Equals(
                            existingDescriptor.ControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId,
                            StringComparison.Ordinal))
                    {
                        logger.LogInformation(
                            "Control-plane discovery entry already exists for another control-plane host. RedisDiscoveryKey={RedisDiscoveryKey}, ExistingControlPlaneId={ExistingControlPlaneId}, CurrentControlPlaneId={CurrentControlPlaneId}",
                            options.RedisDiscoveryKey,
                            existingDescriptor.ControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId);

                        await Task
                            .Delay(refreshInterval, stoppingToken)
                            .ConfigureAwait(false);

                        continue;
                    }

                    var nowUtc = DateTimeOffset.UtcNow;

                    var descriptor =
                        existingDescriptor ?? new AiControlPlaneDiscoveryDescriptor
                        {
                            RedisDiscoveryKey = options.RedisDiscoveryKey,
                            ControlPlaneId = controlPlaneHostIdentity.ControlPlaneHostId,
                            HostId = controlPlaneHostIdentity.ControlPlaneHostId,
                            RuntimeInstanceId = "mcp-control-plane",
                            ProviderName = "control-plane",
                            CreatedAtUtc = nowUtc
                        };

                    descriptor.RedisDiscoveryKey = options.RedisDiscoveryKey;
                    descriptor.ControlPlaneId = controlPlaneHostIdentity.ControlPlaneHostId;
                    descriptor.HostId = controlPlaneHostIdentity.ControlPlaneHostId;
                    descriptor.HeartbeatAtUtc = nowUtc;

                    await discoveryStore
                        .PublishAsync(
                            descriptor,
                            ttl,
                            stoppingToken)
                        .ConfigureAwait(false);

                    logger.LogDebug(
                        "Control-plane discovery descriptor published. RedisDiscoveryKey={RedisDiscoveryKey}, ControlPlaneId={ControlPlaneId}, HeartbeatAtUtc={HeartbeatAtUtc}",
                        descriptor.RedisDiscoveryKey,
                        descriptor.ControlPlaneId,
                        descriptor.HeartbeatAtUtc);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed to publish control-plane discovery descriptor. RedisDiscoveryKey={RedisDiscoveryKey}, ControlPlaneId={ControlPlaneId}",
                        options.RedisDiscoveryKey,
                        controlPlaneHostIdentity.ControlPlaneHostId);
                }

                await Task
                    .Delay(refreshInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            var options = engineOptions.Value.ControlPlane;

            if (options.EnableDiscovery &&
                options.PublishDiscovery &&
                !string.IsNullOrWhiteSpace(options.RedisDiscoveryKey))
            {
                try
                {
                    var descriptor =
                        await discoveryStore
                            .GetAsync(
                                options.RedisDiscoveryKey,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (descriptor is not null &&
                        string.Equals(
                            descriptor.ControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId,
                            StringComparison.Ordinal))
                    {
                        await discoveryStore
                            .DeleteAsync(
                                options.RedisDiscoveryKey,
                                cancellationToken)
                            .ConfigureAwait(false);

                        logger.LogInformation(
                            "Control-plane discovery descriptor deleted on stop. RedisDiscoveryKey={RedisDiscoveryKey}, ControlPlaneId={ControlPlaneId}",
                            options.RedisDiscoveryKey,
                            controlPlaneHostIdentity.ControlPlaneHostId);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to delete control-plane discovery descriptor on stop. RedisDiscoveryKey={RedisDiscoveryKey}, ControlPlaneId={ControlPlaneId}",
                        options.RedisDiscoveryKey,
                        controlPlaneHostIdentity.ControlPlaneHostId);
                }
            }

            await base
                .StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private static TimeSpan ResolveRefreshInterval(AiControlPlaneOptions options)
        {
            if (!options.EnableDiscoveryTtl)
            {
                return TimeSpan.FromSeconds(10);
            }

            var ttl = options.DiscoveryTtl;

            if (ttl <= TimeSpan.Zero)
            {
                return TimeSpan.FromSeconds(10);
            }

            var refreshInterval =
                TimeSpan.FromMilliseconds(
                    Math.Max(
                        500,
                        ttl.TotalMilliseconds / 3));

            return refreshInterval;
        }
    }
}