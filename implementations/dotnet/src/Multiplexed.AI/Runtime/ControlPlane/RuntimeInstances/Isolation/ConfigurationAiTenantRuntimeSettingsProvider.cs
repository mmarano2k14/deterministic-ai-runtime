using Microsoft.Extensions.Configuration;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Provides tenant runtime settings from application configuration.
    /// </summary>
    /// <remarks>
    /// This provider is intended for tests, process-host scenarios, local demos,
    /// and environments where tenant runtime policy is supplied through
    /// configuration instead of hardcoded defaults.
    ///
    /// It reads tenants from the <c>AiTenantRuntimeSettings:Tenants</c> section.
    /// This allows production scenario definitions to control Dedicated, Shared,
    /// and Hybrid tenant behavior without changing runtime code.
    /// </remarks>
    public sealed class ConfigurationAiTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
    {
        private const string SectionName = "AiTenantRuntimeSettings";
        private const string TenantsSectionName = "Tenants";

        private readonly IReadOnlyList<ConfiguredTenantRuntimeSettings> tenants;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationAiTenantRuntimeSettingsProvider"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        public ConfigurationAiTenantRuntimeSettingsProvider(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            this.tenants =
                configuration
                    .GetSection(SectionName)
                    .GetSection(TenantsSectionName)
                    .GetChildren()
                    .Select(ReadTenant)
                    .Where(tenant => !string.IsNullOrWhiteSpace(tenant.TenantId))
                    .ToArray();
        }

        /// <inheritdoc />
        public AiTenantRuntimeSettings GetSettings(string? tenantId, string? tenantGroupId)
        {
            var configuredTenant =
                FindConfiguredTenant(
                    tenantId,
                    tenantGroupId);

            if (configuredTenant is not null)
            {
                return CreateConfiguredSettings(
                    configuredTenant,
                    tenantId,
                    tenantGroupId);
            }

            return CreateFallbackSharedSettings(
                tenantId,
                tenantGroupId);
        }

        /// <summary>
        /// Finds configured settings for the requested tenant.
        /// </summary>
        /// <param name="tenantId">The requested tenant id.</param>
        /// <param name="tenantGroupId">The requested tenant group id.</param>
        /// <returns>The configured tenant settings when found; otherwise, <see langword="null"/>.</returns>
        private ConfiguredTenantRuntimeSettings? FindConfiguredTenant(
            string? tenantId,
            string? tenantGroupId)
        {
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                var byTenantId =
                    this.tenants.FirstOrDefault(tenant =>
                        string.Equals(
                            tenant.TenantId,
                            tenantId,
                            StringComparison.OrdinalIgnoreCase));

                if (byTenantId is not null)
                {
                    return byTenantId;
                }
            }

            if (!string.IsNullOrWhiteSpace(tenantGroupId))
            {
                return this.tenants.FirstOrDefault(tenant =>
                    string.Equals(
                        tenant.TenantGroupId,
                        tenantGroupId,
                        StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        /// <summary>
        /// Reads one tenant settings entry from configuration.
        /// </summary>
        /// <param name="section">The tenant configuration section.</param>
        /// <returns>The configured tenant runtime settings entry.</returns>
        private static ConfiguredTenantRuntimeSettings ReadTenant(IConfigurationSection section)
        {
            return new ConfiguredTenantRuntimeSettings
            {
                TenantId = section["TenantId"],
                TenantGroupId = section["TenantGroupId"],
                IsolationMode = ReadEnum(section, "IsolationMode", AiRuntimeInstanceIsolationMode.Shared),
                PreferDedicatedCapacity = ReadBool(section, "PreferDedicatedCapacity", defaultValue: false),
                AllowSharedFallback = ReadBool(section, "AllowSharedFallback", defaultValue: true),
                MaxRuntimeInstances = ReadInt(section, "MaxRuntimeInstances", defaultValue: 1),
                WorkerCountPerInstance = ReadInt(section, "WorkerCountPerInstance", defaultValue: 10),
                MaxConcurrentRunsPerInstance = ReadInt(section, "MaxConcurrentRunsPerInstance", defaultValue: 3),
                LocalQueueCapacity = ReadNullableInt(section, "LocalQueueCapacity"),
                RuntimeInstanceIdPrefix = section["RuntimeInstanceIdPrefix"]
            };
        }

        /// <summary>
        /// Creates runtime settings from one configured tenant entry.
        /// </summary>
        /// <param name="configuredTenant">The configured tenant entry.</param>
        /// <param name="requestedTenantId">The originally requested tenant id.</param>
        /// <param name="requestedTenantGroupId">The originally requested tenant group id.</param>
        /// <returns>The tenant runtime settings.</returns>
        private static AiTenantRuntimeSettings CreateConfiguredSettings(
            ConfiguredTenantRuntimeSettings configuredTenant,
            string? requestedTenantId,
            string? requestedTenantGroupId)
        {
            var resolvedTenantId =
                !string.IsNullOrWhiteSpace(configuredTenant.TenantId)
                    ? configuredTenant.TenantId.Trim()
                    : ResolveTenantId(requestedTenantId);

            var resolvedTenantGroupId =
                !string.IsNullOrWhiteSpace(configuredTenant.TenantGroupId)
                    ? configuredTenant.TenantGroupId.Trim()
                    : requestedTenantGroupId;

            var runtimeInstanceIdPrefix =
                !string.IsNullOrWhiteSpace(configuredTenant.RuntimeInstanceIdPrefix)
                    ? configuredTenant.RuntimeInstanceIdPrefix.Trim()
                    : $"{resolvedTenantId}-runtime";

            return new AiTenantRuntimeSettings
            {
                TenantId = resolvedTenantId,
                TenantGroupId = resolvedTenantGroupId,
                IsolationMode = configuredTenant.IsolationMode,
                PreferDedicatedCapacity = configuredTenant.PreferDedicatedCapacity,
                AllowSharedFallback = configuredTenant.AllowSharedFallback,
                MaxRuntimeInstances = configuredTenant.MaxRuntimeInstances,
                WorkerCountPerInstance = configuredTenant.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = configuredTenant.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = configuredTenant.LocalQueueCapacity,
                RuntimeInstanceIdPrefix = runtimeInstanceIdPrefix,
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.SettingsSource] = "configuration",
                    [AiRuntimeInstanceIsolationMetadataKeys.RuntimeTenant] = resolvedTenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = configuredTenant.IsolationMode.ToString(),
                    [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = configuredTenant.PreferDedicatedCapacity.ToString(),
                    [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = configuredTenant.AllowSharedFallback.ToString()
                }
            };
        }

        /// <summary>
        /// Creates safe shared fallback settings when no configured tenant entry matches.
        /// </summary>
        /// <param name="tenantId">The requested tenant id.</param>
        /// <param name="tenantGroupId">The requested tenant group id.</param>
        /// <returns>The fallback tenant runtime settings.</returns>
        private static AiTenantRuntimeSettings CreateFallbackSharedSettings(
            string? tenantId,
            string? tenantGroupId)
        {
            var resolvedTenantId =
                ResolveTenantId(
                    tenantId);

            return new AiTenantRuntimeSettings
            {
                TenantId = resolvedTenantId,
                TenantGroupId = tenantGroupId,
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                PreferDedicatedCapacity = false,
                AllowSharedFallback = true,
                MaxRuntimeInstances = 1,
                WorkerCountPerInstance = 10,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = null,
                RuntimeInstanceIdPrefix = "runtime-instance",
                Metadata = new Dictionary<string, string>
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.SettingsSource] = "configuration-fallback",
                    [AiRuntimeInstanceIsolationMetadataKeys.RuntimeTenant] = resolvedTenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = AiRuntimeInstanceIsolationMode.Shared.ToString()
                }
            };
        }

        /// <summary>
        /// Resolves a tenant id from an optional requested tenant id.
        /// </summary>
        /// <param name="tenantId">The requested tenant id.</param>
        /// <returns>The resolved tenant id.</returns>
        private static string ResolveTenantId(string? tenantId)
        {
            return string.IsNullOrWhiteSpace(tenantId)
                ? "shared"
                : tenantId.Trim();
        }

        /// <summary>
        /// Reads an enum value from configuration.
        /// </summary>
        /// <typeparam name="TEnum">The enum type.</typeparam>
        /// <param name="section">The configuration section.</param>
        /// <param name="key">The configuration key.</param>
        /// <param name="defaultValue">The default value.</param>
        /// <returns>The resolved enum value.</returns>
        private static TEnum ReadEnum<TEnum>(
            IConfiguration section,
            string key,
            TEnum defaultValue)
            where TEnum : struct, Enum
        {
            var value = section[key];

            return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
                ? parsed
                : defaultValue;
        }

        /// <summary>
        /// Reads a boolean value from configuration.
        /// </summary>
        /// <param name="section">The configuration section.</param>
        /// <param name="key">The configuration key.</param>
        /// <param name="defaultValue">The default value.</param>
        /// <returns>The resolved boolean value.</returns>
        private static bool ReadBool(
            IConfiguration section,
            string key,
            bool defaultValue)
        {
            return bool.TryParse(section[key], out var parsed)
                ? parsed
                : defaultValue;
        }

        /// <summary>
        /// Reads an integer value from configuration.
        /// </summary>
        /// <param name="section">The configuration section.</param>
        /// <param name="key">The configuration key.</param>
        /// <param name="defaultValue">The default value.</param>
        /// <returns>The resolved integer value.</returns>
        private static int ReadInt(
            IConfiguration section,
            string key,
            int defaultValue)
        {
            return int.TryParse(section[key], out var parsed)
                ? parsed
                : defaultValue;
        }

        /// <summary>
        /// Reads a nullable integer value from configuration.
        /// </summary>
        /// <param name="section">The configuration section.</param>
        /// <param name="key">The configuration key.</param>
        /// <returns>The resolved nullable integer value.</returns>
        private static int? ReadNullableInt(
            IConfiguration section,
            string key)
        {
            return int.TryParse(section[key], out var parsed)
                ? parsed
                : null;
        }

        /// <summary>
        /// Represents one tenant runtime settings entry loaded from configuration.
        /// </summary>
        private sealed class ConfiguredTenantRuntimeSettings
        {
            /// <summary>
            /// Gets the configured tenant id.
            /// </summary>
            public string? TenantId { get; init; }

            /// <summary>
            /// Gets the configured tenant group id.
            /// </summary>
            public string? TenantGroupId { get; init; }

            /// <summary>
            /// Gets the configured runtime isolation mode.
            /// </summary>
            public AiRuntimeInstanceIsolationMode IsolationMode { get; init; }

            /// <summary>
            /// Gets a value indicating whether dedicated capacity should be preferred.
            /// </summary>
            public bool PreferDedicatedCapacity { get; init; }

            /// <summary>
            /// Gets a value indicating whether shared fallback is allowed.
            /// </summary>
            public bool AllowSharedFallback { get; init; }

            /// <summary>
            /// Gets the maximum number of runtime instances.
            /// </summary>
            public int MaxRuntimeInstances { get; init; }

            /// <summary>
            /// Gets the worker count per runtime instance.
            /// </summary>
            public int WorkerCountPerInstance { get; init; }

            /// <summary>
            /// Gets the maximum concurrent runs per runtime instance.
            /// </summary>
            public int MaxConcurrentRunsPerInstance { get; init; }

            /// <summary>
            /// Gets the local queue capacity.
            /// </summary>
            public int? LocalQueueCapacity { get; init; }

            /// <summary>
            /// Gets the runtime instance id prefix.
            /// </summary>
            public string? RuntimeInstanceIdPrefix { get; init; }
        }
    }
}