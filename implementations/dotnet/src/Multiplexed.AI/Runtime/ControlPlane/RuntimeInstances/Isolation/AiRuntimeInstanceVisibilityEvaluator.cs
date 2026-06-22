using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Default runtime instance visibility evaluator.
    /// </summary>
    public sealed class AiRuntimeInstanceVisibilityEvaluator : IAiRuntimeInstanceVisibilityEvaluator
    {
        private const string TenantIdAlias = AiRuntimeInstanceIsolationMetadataKeys.TenantId;

        private const string TenantGroupIdAlias = AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId;

        private readonly IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceVisibilityEvaluator" /> class.
        /// </summary>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        public AiRuntimeInstanceVisibilityEvaluator(
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider)
        {
            this.tenantRuntimeSettingsProvider =
                tenantRuntimeSettingsProvider
                ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
        }

        /// <inheritdoc />
        public bool IsVisible(
            string? tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceVisibilityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            var tenantSettings =
                this.tenantRuntimeSettingsProvider.GetSettings(
                    tenantId,
                    tenantGroupId);

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Shared)
            {
                return IsSharedVisibleForTenant(
                    tenantSettings);
            }

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Dedicated)
            {
                return IsDedicatedMatch(
                    tenantId,
                    tenantGroupId,
                    descriptor);
            }

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Hybrid)
            {
                return IsDedicatedMatch(
                    tenantId,
                    tenantGroupId,
                    descriptor);
            }

            return false;
        }

        /// <inheritdoc />
        public AiRuntimeInstanceVisibilityDescriptor CreateDescriptor(
            string? runtimeInstanceId,
            IReadOnlyDictionary<string, string>? metadata)
        {
            var safeMetadata =
                metadata ?? new Dictionary<string, string>();

            return new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = GetFirstValue(
                    safeMetadata,
                    AiRuntimeInstanceIsolationMetadataKeys.TenantId,
                    TenantIdAlias),
                TenantGroupId = GetFirstValue(
                    safeMetadata,
                    AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId,
                    TenantGroupIdAlias),
                IsolationMode = ParseIsolationMode(
                    GetValue(
                        safeMetadata,
                        AiRuntimeInstanceIsolationMetadataKeys.IsolationMode)),
                AllowSharedFallback = ParseBool(
                    GetValue(
                        safeMetadata,
                        AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                    defaultValue: true),
                PreferDedicatedCapacity = ParseBool(
                    GetValue(
                        safeMetadata,
                        AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                    defaultValue: false),
                Metadata = safeMetadata
            };
        }

        /// <summary>
        /// Determines whether a shared runtime resource is visible for the tenant settings.
        /// </summary>
        /// <param name="tenantSettings">The tenant runtime settings.</param>
        /// <returns><see langword="true" /> when the shared resource is visible; otherwise, <see langword="false" />.</returns>
        private static bool IsSharedVisibleForTenant(
            AiTenantRuntimeSettings tenantSettings)
        {
            if (tenantSettings.IsolationMode == AiRuntimeInstanceIsolationMode.Shared)
            {
                return true;
            }

            if (tenantSettings.IsolationMode == AiRuntimeInstanceIsolationMode.Hybrid)
            {
                return tenantSettings.AllowSharedFallback;
            }

            if (tenantSettings.IsolationMode == AiRuntimeInstanceIsolationMode.Dedicated)
            {
                return tenantSettings.AllowSharedFallback;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a tenant identity matches a dedicated or hybrid runtime resource owner.
        /// </summary>
        /// <param name="tenantId">The current tenant identifier.</param>
        /// <param name="tenantGroupId">The current tenant group identifier.</param>
        /// <param name="descriptor">The runtime resource visibility descriptor.</param>
        /// <returns><see langword="true" /> when the runtime resource belongs to the tenant; otherwise, <see langword="false" />.</returns>
        private static bool IsDedicatedMatch(
            string? tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceVisibilityDescriptor descriptor)
        {
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.IsNullOrWhiteSpace(descriptor.TenantId) &&
                string.Equals(
                    tenantId,
                    descriptor.TenantId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(tenantGroupId) &&
                !string.IsNullOrWhiteSpace(descriptor.TenantGroupId) &&
                string.Equals(
                    tenantGroupId,
                    descriptor.TenantGroupId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the first available metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="keys">The metadata keys to try in order.</param>
        /// <returns>The first metadata value found, or <see langword="null" /> when missing.</returns>
        private static string? GetFirstValue(
            IReadOnlyDictionary<string, string> metadata,
            params string[] keys)
        {
            foreach (var key in keys)
            {
                var value =
                    GetValue(
                        metadata,
                        key);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a metadata value using case-insensitive key matching.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or <see langword="null" /> when missing.</returns>
        private static string? GetValue(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            if (metadata.TryGetValue(key, out var value))
            {
                return value;
            }

            foreach (var item in metadata)
            {
                if (string.Equals(
                        item.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses the runtime instance isolation mode.
        /// </summary>
        /// <param name="value">The serialized isolation mode.</param>
        /// <returns>The parsed isolation mode, or shared mode when missing or invalid.</returns>
        private static AiRuntimeInstanceIsolationMode ParseIsolationMode(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AiRuntimeInstanceIsolationMode.Shared;
            }

            if (Enum.TryParse<AiRuntimeInstanceIsolationMode>(
                    value,
                    ignoreCase: true,
                    out var parsed))
            {
                return parsed;
            }

            return AiRuntimeInstanceIsolationMode.Shared;
        }

        /// <summary>
        /// Parses a boolean metadata value.
        /// </summary>
        /// <param name="value">The serialized boolean value.</param>
        /// <param name="defaultValue">The default value used when the metadata value is missing or invalid.</param>
        /// <returns>The parsed boolean value.</returns>
        private static bool ParseBool(
            string? value,
            bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            if (bool.TryParse(
                    value,
                    out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }
    }
}