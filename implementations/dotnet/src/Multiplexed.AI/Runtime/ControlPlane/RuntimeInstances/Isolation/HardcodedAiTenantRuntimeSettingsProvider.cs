using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Temporary hardcoded tenant runtime settings provider.
    /// This implementation is intentionally simple and can later be replaced by
    /// MongoDB, configuration, tenant plan settings, dashboard settings, or a policy engine.
    /// </summary>
    public sealed class HardcodedAiTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
    {
        private const string TenantA = "tenant-a";
        private const string TenantB = "tenant-b";

        /// <inheritdoc />
        public AiTenantRuntimeSettings GetSettings(
            string? tenantId,
            string? tenantGroupId)
        {
            if (string.Equals(tenantId, TenantA, StringComparison.OrdinalIgnoreCase))
            {
                return new AiTenantRuntimeSettings
                {
                    TenantId = TenantA,
                    TenantGroupId = tenantGroupId,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                    PreferDedicatedCapacity = true,
                    AllowSharedFallback = false,
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 10,
                    MaxConcurrentRunsPerInstance = 5,
                    LocalQueueCapacity = 500,
                    RuntimeInstanceIdPrefix = "tenant-a-runtime",
                    Metadata = new Dictionary<string, string>
                    {
                        ["runtime.settings.source"] = "hardcoded",
                        ["runtime.tenant"] = TenantA
                    }
                };
            }

            if (string.Equals(tenantId, TenantB, StringComparison.OrdinalIgnoreCase))
            {
                return new AiTenantRuntimeSettings
                {
                    TenantId = TenantB,
                    TenantGroupId = tenantGroupId,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Hybrid,
                    PreferDedicatedCapacity = true,
                    AllowSharedFallback = true,
                    MaxRuntimeInstances = 2,
                    WorkerCountPerInstance = 5,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 250,
                    RuntimeInstanceIdPrefix = "tenant-b-runtime",
                    Metadata = new Dictionary<string, string>
                    {
                        ["runtime.settings.source"] = "hardcoded",
                        ["runtime.tenant"] = TenantB
                    }
                };
            }

            return new AiTenantRuntimeSettings
            {
                TenantId = string.IsNullOrWhiteSpace(tenantId)
                    ? "shared"
                    : tenantId,
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
                    ["runtime.settings.source"] = "hardcoded",
                    ["runtime.tenant"] = string.IsNullOrWhiteSpace(tenantId)
                        ? "shared"
                        : tenantId
                }
            };
        }
    }
}