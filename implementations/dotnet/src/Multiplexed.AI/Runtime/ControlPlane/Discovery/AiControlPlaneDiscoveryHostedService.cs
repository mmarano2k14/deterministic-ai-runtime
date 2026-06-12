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

            var effectiveControlPlaneId =
                ResolvePublishedControlPlaneId(
                    options);

            SafeLogInformation(
                "Control-plane discovery publisher configuration resolved. EnableDiscovery={EnableDiscovery}, PublishDiscovery={PublishDiscovery}, RequireDiscovery={RequireDiscovery}, RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, PublishedControlPlaneId={PublishedControlPlaneId}",
                options.EnableDiscovery,
                options.PublishDiscovery,
                options.RequireDiscovery,
                options.RedisDiscoveryKey,
                options.ControlPlaneId,
                controlPlaneHostIdentity.ControlPlaneHostId,
                effectiveControlPlaneId);

            if (!options.EnableDiscovery)
            {
                SafeLogInformation(
                    "Control-plane discovery publisher disabled because discovery is disabled. RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, PublishedControlPlaneId={PublishedControlPlaneId}",
                    options.RedisDiscoveryKey,
                    options.ControlPlaneId,
                    controlPlaneHostIdentity.ControlPlaneHostId,
                    effectiveControlPlaneId);

                return;
            }

            if (!options.PublishDiscovery)
            {
                SafeLogInformation(
                    "Control-plane discovery publisher disabled because this host is not configured as publisher. RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, PublishedControlPlaneId={PublishedControlPlaneId}",
                    options.RedisDiscoveryKey,
                    options.ControlPlaneId,
                    controlPlaneHostIdentity.ControlPlaneHostId,
                    effectiveControlPlaneId);

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

            SafeLogInformation(
                "Control-plane discovery publisher started. RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, PublishedControlPlaneId={PublishedControlPlaneId}, EnableTtl={EnableTtl}, Ttl={Ttl}, RefreshInterval={RefreshInterval}",
                options.RedisDiscoveryKey,
                options.ControlPlaneId,
                controlPlaneHostIdentity.ControlPlaneHostId,
                effectiveControlPlaneId,
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

                    if (existingDescriptor is null)
                    {
                        SafeLogInformation(
                            "No existing control-plane discovery descriptor found. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                            options.RedisDiscoveryKey,
                            effectiveControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId);
                    }
                    else
                    {
                        SafeLogInformation(
                            "Existing control-plane discovery descriptor loaded. RedisDiscoveryKey={RedisDiscoveryKey}, ExistingControlPlaneId={ExistingControlPlaneId}, ExistingHostId={ExistingHostId}, ExistingRuntimeInstanceId={ExistingRuntimeInstanceId}, ExistingProviderName={ExistingProviderName}, ExistingHeartbeatAtUtc={ExistingHeartbeatAtUtc}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                            options.RedisDiscoveryKey,
                            existingDescriptor.ControlPlaneId,
                            existingDescriptor.HostId,
                            existingDescriptor.RuntimeInstanceId,
                            existingDescriptor.ProviderName,
                            existingDescriptor.HeartbeatAtUtc,
                            effectiveControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId);
                    }

                    if (existingDescriptor is not null &&
                        !string.IsNullOrWhiteSpace(existingDescriptor.ControlPlaneId) &&
                        !string.Equals(
                            existingDescriptor.ControlPlaneId,
                            effectiveControlPlaneId,
                            StringComparison.Ordinal))
                    {
                        SafeLogWarning(
                            "Control-plane discovery entry already exists for another logical control-plane. RedisDiscoveryKey={RedisDiscoveryKey}, ExistingControlPlaneId={ExistingControlPlaneId}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                            options.RedisDiscoveryKey,
                            existingDescriptor.ControlPlaneId,
                            options.ControlPlaneId,
                            effectiveControlPlaneId,
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
                            ControlPlaneId = effectiveControlPlaneId,
                            HostId = controlPlaneHostIdentity.ControlPlaneHostId,
                            RuntimeInstanceId = "mcp-control-plane",
                            ProviderName = "control-plane",
                            CreatedAtUtc = nowUtc
                        };

                    descriptor.RedisDiscoveryKey = options.RedisDiscoveryKey;
                    descriptor.ControlPlaneId = effectiveControlPlaneId;
                    descriptor.HostId = controlPlaneHostIdentity.ControlPlaneHostId;
                    descriptor.HeartbeatAtUtc = nowUtc;

                    await discoveryStore
                        .PublishAsync(
                            descriptor,
                            ttl,
                            stoppingToken)
                        .ConfigureAwait(false);

                    SafeLogInformation(
                        "Control-plane discovery descriptor published. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}, RuntimeInstanceId={RuntimeInstanceId}, HeartbeatAtUtc={HeartbeatAtUtc}",
                        descriptor.RedisDiscoveryKey,
                        descriptor.ControlPlaneId,
                        descriptor.HostId,
                        descriptor.RuntimeInstanceId,
                        descriptor.HeartbeatAtUtc);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    SafeLogError(
                        exception,
                        "Failed to publish control-plane discovery descriptor. RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        options.RedisDiscoveryKey,
                        options.ControlPlaneId,
                        effectiveControlPlaneId,
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

            var effectiveControlPlaneId =
                ResolvePublishedControlPlaneId(
                    options);

            SafeLogInformation(
                "Control-plane discovery publisher stopping. EnableDiscovery={EnableDiscovery}, PublishDiscovery={PublishDiscovery}, RedisDiscoveryKey={RedisDiscoveryKey}, ConfiguredControlPlaneId={ConfiguredControlPlaneId}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                options.EnableDiscovery,
                options.PublishDiscovery,
                options.RedisDiscoveryKey,
                options.ControlPlaneId,
                effectiveControlPlaneId,
                controlPlaneHostIdentity.ControlPlaneHostId);

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
                            effectiveControlPlaneId,
                            StringComparison.Ordinal))
                    {
                        await discoveryStore
                            .DeleteAsync(
                                options.RedisDiscoveryKey,
                                cancellationToken)
                            .ConfigureAwait(false);

                        SafeLogInformation(
                            "Control-plane discovery descriptor deleted on stop. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                            options.RedisDiscoveryKey,
                            effectiveControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId);
                    }
                    else
                    {
                        SafeLogInformation(
                            "Control-plane discovery descriptor not deleted on stop because it does not belong to this logical control-plane. RedisDiscoveryKey={RedisDiscoveryKey}, ExistingControlPlaneId={ExistingControlPlaneId}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                            options.RedisDiscoveryKey,
                            descriptor?.ControlPlaneId,
                            effectiveControlPlaneId,
                            controlPlaneHostIdentity.ControlPlaneHostId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SafeLogWarning(
                        "Control-plane discovery descriptor delete cancelled on stop. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        options.RedisDiscoveryKey,
                        effectiveControlPlaneId,
                        controlPlaneHostIdentity.ControlPlaneHostId);
                }
                catch (Exception exception)
                {
                    SafeLogWarning(
                        exception,
                        "Failed to delete control-plane discovery descriptor on stop. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                        options.RedisDiscoveryKey,
                        effectiveControlPlaneId,
                        controlPlaneHostIdentity.ControlPlaneHostId);
                }
            }

            try
            {
                await base
                    .StopAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SafeLogWarning(
                    "Control-plane discovery base stop cancelled. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                    options.RedisDiscoveryKey,
                    effectiveControlPlaneId,
                    controlPlaneHostIdentity.ControlPlaneHostId);
            }
            catch (ObjectDisposedException exception)
            {
                SafeLogWarning(
                    exception,
                    "Control-plane discovery base stop ignored because dependencies were already disposed. RedisDiscoveryKey={RedisDiscoveryKey}, PublishedControlPlaneId={PublishedControlPlaneId}, ControlPlaneHostId={ControlPlaneHostId}",
                    options.RedisDiscoveryKey,
                    effectiveControlPlaneId,
                    controlPlaneHostIdentity.ControlPlaneHostId);
            }
        }

        private void SafeLogInformation(
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogInformation(
                    message,
                    args);
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private void SafeLogWarning(
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogWarning(
                    message,
                    args);
            }
            catch (AggregateException aggregateException)
                when (aggregateException.InnerExceptions.Any(inner =>
                    inner is ObjectDisposedException or InvalidOperationException))
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (InvalidOperationException)
            {
                // Logger infrastructure may already be unavailable during shutdown.
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private void SafeLogWarning(
            Exception exception,
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogWarning(
                    exception,
                    message,
                    args);
            }
            catch (AggregateException aggregateException)
                when (aggregateException.InnerExceptions.Any(inner =>
                    inner is ObjectDisposedException or InvalidOperationException))
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (InvalidOperationException)
            {
                // Logger infrastructure may already be unavailable during shutdown.
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private void SafeLogError(
            Exception exception,
            string message,
            params object?[] args)
        {
            try
            {
                logger.LogError(
                    exception,
                    message,
                    args);
            }
            catch (AggregateException aggregateException)
                when (aggregateException.InnerExceptions.Any(inner =>
                    inner is ObjectDisposedException or InvalidOperationException))
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (ObjectDisposedException)
            {
                // Logger provider was already disposed during host shutdown.
            }
            catch (InvalidOperationException)
            {
                // Logger infrastructure may already be unavailable during shutdown.
            }
            catch
            {
                // Never allow logging failures to break shutdown.
            }
        }

        private static string ResolvePublishedControlPlaneId(
            AiControlPlaneOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.ControlPlaneId))
            {
                return options.ControlPlaneId;
            }

            return AiControlPlaneOptions.DefaultControlPlaneId;
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
