using Microsoft.Extensions.Configuration;
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
        private static readonly string[] ConfigurationControlPlaneIdKeys =
        {
            "AiRuntimeInstanceRegistration:ControlPlaneId",
            "AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId",
            "AiRuntimeInstanceRegistration:ProviderMetadata:control-plane.id",
            "AiRuntimeInstanceRegistration:ProviderMetadata:controlplane.id",
            "AiRuntimeInstanceRegistration:ProviderMetadata:runtime.controlPlaneId",
            "AiRuntimeInstanceRegistration:Metadata:controlPlaneId",
            "AiRuntimeInstanceRegistration:Metadata:control-plane.id",
            "AiRuntimeInstanceRegistration:Metadata:controlplane.id",
            "AiRuntimeInstanceRegistration:Metadata:runtime.controlPlaneId",
            "AiRuntimeScaleOutRequestWatcher:ControlPlaneId",
            "AiEngine:ControlPlane:ControlPlaneId",
            "AiMcpHost:ControlPlaneId",
        };

        private static readonly string[] EnvironmentControlPlaneIdKeys =
        {
            "AI_CONTROL_PLANE_ID",
            "CONTROL_PLANE_ID",
            "AiRuntimeInstanceRegistration__ControlPlaneId"
        };

        private static readonly string[] MetadataControlPlaneIdKeys =
        {
            AiControlPlaneMetadataKeys.ControlPlaneId,
            "logicalControlPlaneId",
            AiControlPlaneMetadataKeys.RuntimeControlPlaneId,
            AiControlPlaneMetadataKeys.McpControlPlaneId,
            "recovery.controlPlaneId",
            "scaleout.controlPlaneId",
            "scenario.controlPlaneId",
            AiControlPlaneMetadataKeys.DashedControlPlaneId,
            AiControlPlaneMetadataKeys.CompactControlPlaneId,
            "runtime.control-plane.id",
            "runtime.controlplane.id",
            AiControlPlaneMetadataKeys.McpDashedControlPlaneId,
            "mcp.controlplane.id",
            "recovery.control-plane.id",
            "recovery.controlplane.id",
            "scaleout.control-plane.id",
            "scaleout.controlplane.id",
            "scenario.control-plane.id",
            "scenario.controlplane.id"
        };

        private readonly IAiControlPlaneDiscoveryStore discoveryStore;
        private readonly IAiControlPlaneHostIdentity controlPlaneHostIdentity;
        private readonly IOptions<AiEngineOptions> engineOptions;
        private readonly IConfiguration configuration;
        private string? resolvedControlPlaneId;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAiControlPlaneIdResolver"/> class.
        /// </summary>
        /// <param name="discoveryStore">The control-plane discovery store.</param>
        /// <param name="controlPlaneHostIdentity">The control-plane host identity.</param>
        /// <param name="engineOptions">The AI engine options.</param>
        /// <param name="configuration">The application configuration.</param>
        public DefaultAiControlPlaneIdResolver(
            IAiControlPlaneDiscoveryStore discoveryStore,
            IAiControlPlaneHostIdentity controlPlaneHostIdentity,
            IOptions<AiEngineOptions> engineOptions,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(discoveryStore);
            ArgumentNullException.ThrowIfNull(controlPlaneHostIdentity);
            ArgumentNullException.ThrowIfNull(engineOptions);
            ArgumentNullException.ThrowIfNull(configuration);

            this.discoveryStore = discoveryStore;
            this.controlPlaneHostIdentity = controlPlaneHostIdentity;
            this.engineOptions = engineOptions;
            this.configuration = configuration;
        }

        /// <inheritdoc />
        public Task<string> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            return this.ResolveAsync(
                new AiControlPlaneIdResolutionRequest
                {
                    Source = "default",
                    AllowGeneratedFallback = true
                },
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<string> ResolveAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var cachedControlPlaneId =
                Volatile.Read(ref this.resolvedControlPlaneId);

            if (IsAllowed(
                    cachedControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return cachedControlPlaneId!;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var requestedControlPlaneId =
                NormalizeOrNull(request.RequestedControlPlaneId);

            if (IsAllowed(
                    requestedControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(requestedControlPlaneId!);
            }

            var metadataControlPlaneId =
                ResolveFromMetadata(request.Metadata);

            if (IsAllowed(
                    metadataControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(metadataControlPlaneId!);
            }

            var configurationControlPlaneId =
                this.ResolveFromConfiguration();

            if (IsAllowed(
                    configurationControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(configurationControlPlaneId!);
            }

            var environmentControlPlaneId =
                ResolveFromEnvironment();

            if (IsAllowed(
                    environmentControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(environmentControlPlaneId!);
            }

            var discoveryControlPlaneId =
                await this.ResolveFromDiscoveryAsync(
                        request.AllowGeneratedFallback,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (IsAllowed(
                    discoveryControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(discoveryControlPlaneId!);
            }

            var fallbackControlPlaneId =
                NormalizeOrNull(this.controlPlaneHostIdentity.ControlPlaneHostId);

            if (IsAllowed(
                    fallbackControlPlaneId,
                    request.AllowGeneratedFallback))
            {
                return this.CacheAndReturn(fallbackControlPlaneId!);
            }

            throw new InvalidOperationException(
                $"Unable to resolve a logical control-plane identifier for source '{request.Source ?? "unknown"}'.");
        }

        /// <inheritdoc />
        public async Task<IReadOnlyDictionary<string, string>> ResolveMetadataAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var controlPlaneId =
                await this.ResolveAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            return CreateCanonicalMetadata(controlPlaneId);
        }

        /// <summary>
        /// Resolves a control-plane identifier from configured application settings.
        /// </summary>
        /// <returns>The resolved control-plane identifier, or null.</returns>
        private string? ResolveFromConfiguration()
        {
            foreach (var key in ConfigurationControlPlaneIdKeys)
            {
                var value =
                    NormalizeOrNull(this.configuration[key]);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a control-plane identifier from process environment variables.
        /// </summary>
        /// <returns>The resolved control-plane identifier, or null.</returns>
        private static string? ResolveFromEnvironment()
        {
            foreach (var key in EnvironmentControlPlaneIdKeys)
            {
                var value =
                    NormalizeOrNull(Environment.GetEnvironmentVariable(key));

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a control-plane identifier from operation metadata.
        /// </summary>
        /// <param name="metadata">The operation metadata.</param>
        /// <returns>The resolved control-plane identifier, or null.</returns>
        private static string? ResolveFromMetadata(
            IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null || metadata.Count == 0)
            {
                return null;
            }

            foreach (var key in MetadataControlPlaneIdKeys)
            {
                foreach (var pair in metadata)
                {
                    if (!string.Equals(
                            pair.Key,
                            key,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value =
                        NormalizeOrNull(pair.Value);

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a control-plane identifier from discovery.
        /// </summary>
        /// <param name="allowGeneratedFallback">Whether generated host identifiers are allowed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved control-plane identifier, or null.</returns>
        private async Task<string?> ResolveFromDiscoveryAsync(
            bool allowGeneratedFallback,
            CancellationToken cancellationToken)
        {
            var options =
                this.engineOptions.Value.ControlPlane;

            if (!options.EnableDiscovery)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(options.RedisDiscoveryKey))
            {
                if (options.RequireDiscovery)
                {
                    throw new InvalidOperationException(
                        "A Redis discovery key is required to resolve the control-plane identifier.");
                }

                return null;
            }

            var deadlineUtc =
                DateTimeOffset.UtcNow.Add(options.DiscoveryResolutionTimeout);

            while (DateTimeOffset.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var descriptor =
                    await this.discoveryStore
                        .GetAsync(
                            options.RedisDiscoveryKey,
                            cancellationToken)
                        .ConfigureAwait(false);

                var descriptorControlPlaneId =
                    NormalizeOrNull(descriptor?.ControlPlaneId);

                if (!string.IsNullOrWhiteSpace(descriptorControlPlaneId))
                {
                    return descriptorControlPlaneId;
                }

                if (options.PublishDiscovery && allowGeneratedFallback)
                {
                    return NormalizeOrNull(
                        this.controlPlaneHostIdentity.ControlPlaneHostId);
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
                    $"Unable to resolve the control-plane identifier from Redis discovery key '{options.RedisDiscoveryKey}' within '{options.DiscoveryResolutionTimeout}'.");
            }

            return null;
        }

        /// <summary>
        /// Creates canonical metadata aliases for a logical control-plane identifier.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <returns>The canonical control-plane metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateCanonicalMetadata(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var normalizedControlPlaneId =
                controlPlaneId.Trim();

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AiControlPlaneMetadataKeys.ControlPlaneId] = normalizedControlPlaneId,
                ["logicalControlPlaneId"] = normalizedControlPlaneId,
                [AiControlPlaneMetadataKeys.RuntimeControlPlaneId] = normalizedControlPlaneId,
                [AiControlPlaneMetadataKeys.McpControlPlaneId] = normalizedControlPlaneId,
                ["recovery.controlPlaneId"] = normalizedControlPlaneId,
                ["scaleout.controlPlaneId"] = normalizedControlPlaneId,
                ["scenario.controlPlaneId"] = normalizedControlPlaneId,
                [AiControlPlaneMetadataKeys.DashedControlPlaneId] = normalizedControlPlaneId,
                [AiControlPlaneMetadataKeys.CompactControlPlaneId] = normalizedControlPlaneId,
                ["runtime.control-plane.id"] = normalizedControlPlaneId,
                ["runtime.controlplane.id"] = normalizedControlPlaneId,
                [AiControlPlaneMetadataKeys.McpDashedControlPlaneId] = normalizedControlPlaneId,
                ["mcp.controlplane.id"] = normalizedControlPlaneId,
                ["recovery.control-plane.id"] = normalizedControlPlaneId,
                ["recovery.controlplane.id"] = normalizedControlPlaneId,
                ["scaleout.control-plane.id"] = normalizedControlPlaneId,
                ["scaleout.controlplane.id"] = normalizedControlPlaneId,
                ["scenario.control-plane.id"] = normalizedControlPlaneId,
                ["scenario.controlplane.id"] = normalizedControlPlaneId
            };
        }

        /// <summary>
        /// Determines whether a candidate value is allowed by the current resolution mode.
        /// </summary>
        /// <param name="value">The candidate value.</param>
        /// <param name="allowGeneratedFallback">Whether generated host identifiers are allowed.</param>
        /// <returns><c>true</c> when the value is usable; otherwise, <c>false</c>.</returns>
        private static bool IsAllowed(
            string? value,
            bool allowGeneratedFallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!allowGeneratedFallback &&
                IsGeneratedControlPlaneHostId(value))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the supplied identifier is a generated control-plane host identifier.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <returns><c>true</c> when the value looks like a generated host identifier; otherwise, <c>false</c>.</returns>
        private static bool IsGeneratedControlPlaneHostId(
            string? controlPlaneId)
        {
            return !string.IsNullOrWhiteSpace(controlPlaneId) &&
                controlPlaneId.StartsWith(
                    "control-plane-",
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes a value or returns null.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The normalized value, or null when unavailable.</returns>
        private static string? NormalizeOrNull(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
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
                ref this.resolvedControlPlaneId,
                normalizedControlPlaneId);

            return normalizedControlPlaneId;
        }
    }
}