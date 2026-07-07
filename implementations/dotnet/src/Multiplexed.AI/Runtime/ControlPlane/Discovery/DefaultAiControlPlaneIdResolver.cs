using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;
using Multiplexed.AI.Configuration;
using System.Threading;

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
        private string? resolvedControlPlaneId;

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
            var cachedControlPlaneId =
                Volatile.Read(ref resolvedControlPlaneId);

            if (!string.IsNullOrWhiteSpace(cachedControlPlaneId))
            {
                return cachedControlPlaneId;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var explicitControlPlaneId =
                TryResolveExplicitControlPlaneIdFromEnvironment();

            if (!string.IsNullOrWhiteSpace(explicitControlPlaneId))
            {
                return CacheAndReturn(explicitControlPlaneId);
            }

            var options =
                engineOptions.Value.ControlPlane;

            // Discovery can only be disabled for local single-host scenarios.
            // Distributed runtime hosts must keep discovery enabled so they join
            // the control-plane identity published by the MCP/control-plane host.
            if (!options.EnableDiscovery)
            {
                return CacheAndReturn(
                    RequireUsableFallback(controlPlaneHostIdentity.ControlPlaneHostId));
            }

            if (string.IsNullOrWhiteSpace(options.RedisDiscoveryKey))
            {
                if (options.RequireDiscovery)
                {
                    throw new InvalidOperationException(
                        "A Redis discovery key is required to resolve the control-plane identifier.");
                }

                return CacheAndReturn(
                    RequireUsableFallback(controlPlaneHostIdentity.ControlPlaneHostId));
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
                    return CacheAndReturn(descriptor.ControlPlaneId);
                }

                if (options.PublishDiscovery)
                {
                    return CacheAndReturn(
                        RequireUsableFallback(controlPlaneHostIdentity.ControlPlaneHostId));
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

            return CacheAndReturn(
                RequireUsableFallback(controlPlaneHostIdentity.ControlPlaneHostId));
        }

        /// <summary>
        /// Attempts to resolve an explicitly configured control-plane identifier from environment variables.
        /// </summary>
        /// <returns>The explicitly configured control-plane identifier when available; otherwise null.</returns>
        private static string? TryResolveExplicitControlPlaneIdFromEnvironment()
        {
            return FirstNonEmpty(
                Environment.GetEnvironmentVariable("AI_CONTROL_PLANE_ID"),
                Environment.GetEnvironmentVariable("CONTROL_PLANE_ID"),
                Environment.GetEnvironmentVariable("AiRuntimeInstanceRegistration__ControlPlaneId"));
        }

        /// <summary>
        /// Returns the first non-empty value.
        /// </summary>
        /// <param name="values">The candidate values.</param>
        /// <returns>The first non-empty value, or null when none is available.</returns>
        private static string? FirstNonEmpty(
            params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Requires a usable local fallback control-plane identifier.
        /// </summary>
        /// <param name="controlPlaneId">The fallback control-plane identifier.</param>
        /// <returns>The fallback control-plane identifier.</returns>
        private static string RequireUsableFallback(
            string? controlPlaneId)
        {
            if (string.IsNullOrWhiteSpace(controlPlaneId))
            {
                throw new InvalidOperationException(
                    "The resolved control-plane host identifier cannot be null or empty.");
            }

            return controlPlaneId;
        }

        /// <summary>
        /// Caches and returns the resolved control-plane identifier.
        /// </summary>
        /// <param name="controlPlaneId">The resolved control-plane identifier.</param>
        /// <returns>The resolved control-plane identifier.</returns>
        private string CacheAndReturn(
            string controlPlaneId)
        {
            var normalizedControlPlaneId =
                controlPlaneId.Trim();

            Volatile.Write(
                ref resolvedControlPlaneId,
                normalizedControlPlaneId);

            return normalizedControlPlaneId;
        }
    }
}