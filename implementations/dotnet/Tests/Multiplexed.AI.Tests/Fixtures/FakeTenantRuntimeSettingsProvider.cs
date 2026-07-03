using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Fake tenant runtime settings provider used by unit tests.
    /// </summary>
    public sealed class FakeTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
    {
        private readonly Dictionary<string, AiTenantRuntimeSettings> settingsByTenantId =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the default tenant identifier.
        /// </summary>
        public string TenantId { get; set; } = "default";

        /// <summary>
        /// Gets or sets the default tenant group identifier.
        /// </summary>
        public string TenantGroupId { get; set; } = "default";

        /// <summary>
        /// Gets or sets the default isolation mode.
        /// </summary>
        public AiRuntimeInstanceIsolationMode IsolationMode { get; set; } = AiRuntimeInstanceIsolationMode.Shared;

        /// <summary>
        /// Gets or sets a value indicating whether dedicated capacity is preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether shared fallback is allowed.
        /// </summary>
        public bool AllowSharedFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets the default worker count per runtime instance.
        /// </summary>
        public int WorkerCountPerInstance { get; set; } = 10;

        /// <summary>
        /// Gets or sets the default maximum concurrent runs per runtime instance.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; set; } = 5;

        /// <summary>
        /// Gets or sets the default local queue capacity.
        /// </summary>
        public int LocalQueueCapacity { get; set; } = 100;

        /// <summary>
        /// Gets or sets the default runtime instance identifier prefix.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } = "runtime";

        /// <summary>
        /// Adds or replaces settings for a tenant.
        /// </summary>
        /// <param name="settings">The tenant runtime settings.</param>
        public void Set(
            AiTenantRuntimeSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (string.IsNullOrWhiteSpace(settings.TenantId))
            {
                throw new ArgumentException(
                    "Tenant runtime settings must define a tenant identifier.",
                    nameof(settings));
            }

            settingsByTenantId[settings.TenantId] = settings;
        }

        /// <inheritdoc />
        public AiTenantRuntimeSettings GetSettings(
            string? tenantId,
            string? tenantGroupId)
        {
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                settingsByTenantId.TryGetValue(tenantId, out var settings))
            {
                return settings;
            }

            return new AiTenantRuntimeSettings
            {
                TenantId = tenantId ?? TenantId,
                TenantGroupId = tenantGroupId ?? TenantGroupId,
                IsolationMode = IsolationMode,
                PreferDedicatedCapacity = PreferDedicatedCapacity,
                AllowSharedFallback = AllowSharedFallback,
                WorkerCountPerInstance = WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = LocalQueueCapacity,
                RuntimeInstanceIdPrefix = RuntimeInstanceIdPrefix
            };
        }
    }
}