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
        private const string TenantTest = "tenant-test";
        private const string Tenant1 = "tenant-1";
        private const string Tenant2 = "tenant-2";

        /// <inheritdoc />
        public AiTenantRuntimeSettings GetSettings(
            string? tenantId,
            string? tenantGroupId)
        {
            if (TryResolveDedicatedTenantId(tenantId, out var dedicatedTenantId))
            {
                return CreateDedicatedSettings(
                    dedicatedTenantId,
                    tenantGroupId);
            }

            if (TryResolveHybridTenantId(tenantId, out var hybridTenantId))
            {
                return CreateHybridSettings(
                    hybridTenantId,
                    tenantGroupId);
            }

            return CreateSharedSettings(
                tenantId,
                tenantGroupId);
        }

        /// <summary>
        /// Tries to resolve a tenant identifier to a known dedicated tenant identifier.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="resolvedTenantId">The resolved canonical dedicated tenant identifier.</param>
        /// <returns><see langword="true"/> when the tenant is a dedicated tenant; otherwise, <see langword="false"/>.</returns>
        private static bool TryResolveDedicatedTenantId(
            string? tenantId,
            out string resolvedTenantId)
        {
            if (string.Equals(tenantId, TenantA, StringComparison.OrdinalIgnoreCase))
            {
                resolvedTenantId = TenantA;
                return true;
            }

            if (string.Equals(tenantId, TenantTest, StringComparison.OrdinalIgnoreCase))
            {
                resolvedTenantId = TenantTest;
                return true;
            }

            if (string.Equals(tenantId, Tenant1, StringComparison.OrdinalIgnoreCase))
            {
                resolvedTenantId = Tenant1;
                return true;
            }

            resolvedTenantId = string.Empty;
            return false;
        }

        /// <summary>
        /// Tries to resolve a tenant identifier to a known hybrid tenant identifier.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="resolvedTenantId">The resolved canonical hybrid tenant identifier.</param>
        /// <returns><see langword="true"/> when the tenant is a hybrid tenant; otherwise, <see langword="false"/>.</returns>
        private static bool TryResolveHybridTenantId(
            string? tenantId,
            out string resolvedTenantId)
        {
            if (string.Equals(tenantId, TenantB, StringComparison.OrdinalIgnoreCase))
            {
                resolvedTenantId = TenantB;
                return true;
            }

            if (string.Equals(tenantId, Tenant2, StringComparison.OrdinalIgnoreCase))
            {
                resolvedTenantId = Tenant2;
                return true;
            }

            resolvedTenantId = string.Empty;
            return false;
        }

        /// <summary>
        /// Creates dedicated runtime settings for a tenant.
        /// </summary>
        /// <param name="tenantId">The canonical tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The tenant runtime settings.</returns>
        private static AiTenantRuntimeSettings CreateDedicatedSettings(
            string tenantId,
            string? tenantGroupId)
        {
            return new AiTenantRuntimeSettings
            {
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                MaxRuntimeInstances = 3,
                WorkerCountPerInstance = 10,
                MaxConcurrentRunsPerInstance = 5,
                LocalQueueCapacity = 500,
                RuntimeInstanceIdPrefix = $"{tenantId}-runtime",
                Metadata = new Dictionary<string, string>
                {
                    ["runtime.settings.source"] = "hardcoded",
                    ["runtime.tenant"] = tenantId
                }
            };
        }

        /// <summary>
        /// Creates hybrid runtime settings for a tenant.
        /// </summary>
        /// <param name="tenantId">The canonical tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The tenant runtime settings.</returns>
        private static AiTenantRuntimeSettings CreateHybridSettings(
            string tenantId,
            string? tenantGroupId)
        {
            return new AiTenantRuntimeSettings
            {
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                IsolationMode = AiRuntimeInstanceIsolationMode.Hybrid,
                PreferDedicatedCapacity = true,
                AllowSharedFallback = true,
                MaxRuntimeInstances = 2,
                WorkerCountPerInstance = 5,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 250,
                RuntimeInstanceIdPrefix = $"{tenantId}-runtime",
                Metadata = new Dictionary<string, string>
                {
                    ["runtime.settings.source"] = "hardcoded",
                    ["runtime.tenant"] = tenantId
                }
            };
        }

        /// <summary>
        /// Creates shared runtime settings for an unknown or shared tenant.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="tenantGroupId">The tenant group identifier.</param>
        /// <returns>The tenant runtime settings.</returns>
        private static AiTenantRuntimeSettings CreateSharedSettings(
            string? tenantId,
            string? tenantGroupId)
        {
            var resolvedTenantId =
                string.IsNullOrWhiteSpace(tenantId)
                    ? "shared"
                    : tenantId.Trim();

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
                    ["runtime.settings.source"] = "hardcoded",
                    ["runtime.tenant"] = resolvedTenantId
                }
            };
        }
    }
}