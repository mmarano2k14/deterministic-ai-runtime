using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Default runtime instance visibility evaluator.
    /// </summary>
    public sealed class AiRuntimeInstanceVisibilityEvaluator : IAiRuntimeInstanceVisibilityEvaluator
    {
        private readonly IAiTenantRuntimeSettingsProvider _tenantRuntimeSettingsProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceVisibilityEvaluator"/> class.
        /// </summary>
        /// <param name="tenantRuntimeSettingsProvider">The tenant runtime settings provider.</param>
        public AiRuntimeInstanceVisibilityEvaluator(
            IAiTenantRuntimeSettingsProvider tenantRuntimeSettingsProvider)
        {
            _tenantRuntimeSettingsProvider = tenantRuntimeSettingsProvider
                ?? throw new ArgumentNullException(nameof(tenantRuntimeSettingsProvider));
        }

        /// <inheritdoc />
        public bool IsVisible(
            string? tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceVisibilityDescriptor descriptor)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var tenantSettings = _tenantRuntimeSettingsProvider.GetSettings(
                tenantId,
                tenantGroupId);

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Shared)
            {
                return IsSharedVisibleForTenant(tenantSettings);
            }

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Dedicated)
            {
                if (IsDedicatedMatch(tenantId, tenantGroupId, descriptor))
                {
                    return true;
                }

                return false;
            }

            if (descriptor.IsolationMode == AiRuntimeInstanceIsolationMode.Hybrid)
            {
                if (IsDedicatedMatch(tenantId, tenantGroupId, descriptor))
                {
                    return true;
                }

                return descriptor.AllowSharedFallback &&
                       tenantSettings.AllowSharedFallback;
            }

            return false;
        }

        /// <inheritdoc />
        public AiRuntimeInstanceVisibilityDescriptor CreateDescriptor(
            string? runtimeInstanceId,
            IReadOnlyDictionary<string, string>? metadata)
        {
            var safeMetadata = metadata ?? new Dictionary<string, string>();

            return new AiRuntimeInstanceVisibilityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = GetValue(safeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantId),
                TenantGroupId = GetValue(safeMetadata, AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId),
                IsolationMode = ParseIsolationMode(
                    GetValue(safeMetadata, AiRuntimeInstanceIsolationMetadataKeys.IsolationMode)),
                AllowSharedFallback = ParseBool(
                    GetValue(safeMetadata, AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback),
                    defaultValue: true),
                PreferDedicatedCapacity = ParseBool(
                    GetValue(safeMetadata, AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity),
                    defaultValue: false),
                Metadata = safeMetadata
            };
        }

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

        private static bool IsDedicatedMatch(
            string? tenantId,
            string? tenantGroupId,
            AiRuntimeInstanceVisibilityDescriptor descriptor)
        {
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.IsNullOrWhiteSpace(descriptor.TenantId) &&
                string.Equals(tenantId, descriptor.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(tenantGroupId) &&
                !string.IsNullOrWhiteSpace(descriptor.TenantGroupId) &&
                string.Equals(tenantGroupId, descriptor.TenantGroupId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

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
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return item.Value;
                }
            }

            return null;
        }

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

        private static bool ParseBool(
            string? value,
            bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return defaultValue;
            }

            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }
    }
}