using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.AI.Configuration;

namespace Multiplexed.AI.Runtime.ControlPlane.Discovery
{
    /// <summary>
    /// Provides the default control-plane identifier resolver.
    /// </summary>
    public sealed class DefaultAiControlPlaneIdResolver : IAiControlPlaneIdResolver
    {
        private readonly IAiControlPlaneDiscoveryStore discoveryStore;
        private readonly IAiControlPlaneHostIdentity controlPlaneHostIdentity;
        private readonly IOptions<AiEngineOptions> engineOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiControlPlaneIdResolver"/> class.
        /// </summary>
        /// <param name="discoveryStore">The control-plane discovery store.</param>
        /// <param name="controlPlaneHostIdentity">The control-plane host identity.</param>
        /// <param name="engineOptions">The AI engine options.</param>
        public DefaultAiControlPlaneIdResolver(
            IAiControlPlaneDiscoveryStore discoveryStore,
            IAiControlPlaneHostIdentity controlPlaneHostIdentity,
            IOptions<AiEngineOptions> engineOptions)
        {
            ArgumentNullException.ThrowIfNull(discoveryStore);
            ArgumentNullException.ThrowIfNull(controlPlaneHostIdentity);
            ArgumentNullException.ThrowIfNull(engineOptions);

            this.discoveryStore = discoveryStore;
            this.controlPlaneHostIdentity = controlPlaneHostIdentity;
            this.engineOptions = engineOptions;
        }

        /// <inheritdoc />
        public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
        {
            var options = engineOptions.Value.ControlPlane;

            // Discovery can only be disabled for local single-host scenarios.
            // Distributed runtime hosts must keep discovery enabled so they join
            // the control-plane identity published by the MCP/control-plane host.
            if (!options.EnableDiscovery)
            {
                return controlPlaneHostIdentity.ControlPlaneHostId;
            }

            if (string.IsNullOrWhiteSpace(options.RedisDiscoveryKey))
            {
                if (options.RequireDiscovery)
                {
                    throw new InvalidOperationException(
                        "A Redis discovery key is required to resolve the control-plane identifier.");
                }

                return controlPlaneHostIdentity.ControlPlaneHostId;
            }

            var deadlineUtc =
                DateTimeOffset.UtcNow.Add(options.DiscoveryResolutionTimeout);

            while (DateTimeOffset.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var descriptor =
                    await discoveryStore
                        .GetAsync(
                            options.RedisDiscoveryKey,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (descriptor is not null &&
                    !string.IsNullOrWhiteSpace(descriptor.ControlPlaneId))
                {
                    return descriptor.ControlPlaneId;
                }

                if (options.PublishDiscovery)
                {
                    return controlPlaneHostIdentity.ControlPlaneHostId;
                }

                if (!options.RequireDiscovery)
                {
                    break;
                }

                await Task
                    .Delay(
                        options.DiscoveryResolutionPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (options.RequireDiscovery)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve the control-plane identifier from Redis discovery key '{options.RedisDiscoveryKey}' " +
                    $"within '{options.DiscoveryResolutionTimeout}'.");
            }

            return controlPlaneHostIdentity.ControlPlaneHostId;
        }
    }
}