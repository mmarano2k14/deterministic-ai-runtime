using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains common tenant isolation assertions for production runtime scenarios.
    /// </summary>
    public static class ProductionTenantIsolationAssertions
    {
        /// <summary>
        /// Verifies that tenant runs were assigned only to runtime instances matching the tenant runtime prefix,
        /// while allowing hybrid tenants to use shared runtime fallback capacity.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertTenantRuntimePrefixesWereRespected(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants.Where(x => x.ExpectDedicatedRuntimePrefix))
            {
                var allowedRuntimePrefixes = GetAllowedRuntimePrefixes(scenario, tenant);

                var tenantResult =
                    result.Tenants.Single(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.NotEmpty(tenantResult.RuntimeInstanceIds);

                Assert.All(
                    tenantResult.RuntimeInstanceIds,
                    runtimeInstanceId =>
                    {
                        Assert.Contains(
                            allowedRuntimePrefixes,
                            prefix => runtimeInstanceId.Contains(prefix, StringComparison.Ordinal));
                    });

                Assert.All(
                    tenantResult.Runs,
                    run =>
                    {
                        Assert.False(string.IsNullOrWhiteSpace(run.RuntimeInstanceId));

                        Assert.Contains(
                            allowedRuntimePrefixes,
                            prefix => run.RuntimeInstanceId!.Contains(prefix, StringComparison.Ordinal));
                    });
            }
        }

        /// <summary>
        /// Verifies that one tenant did not use another tenant runtime prefix,
        /// while allowing hybrid tenants to use shared runtime fallback capacity.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertNoCrossTenantRuntimePrefixUsage(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants)
            {
                var allowedRuntimePrefixes = GetAllowedRuntimePrefixes(scenario, tenant);

                var forbiddenPrefixes =
                    scenario.Tenants
                        .Select(x => x.RuntimeInstanceIdPrefix)
                        .Where(prefix => !allowedRuntimePrefixes.Contains(prefix, StringComparer.Ordinal))
                        .ToArray();

                var tenantResult =
                    result.Tenants.Single(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                foreach (var runtimeInstanceId in tenantResult.RuntimeInstanceIds)
                {
                    Assert.DoesNotContain(
                        forbiddenPrefixes,
                        prefix => runtimeInstanceId.Contains(prefix, StringComparison.Ordinal));
                }

                foreach (var run in tenantResult.Runs)
                {
                    if (string.IsNullOrWhiteSpace(run.RuntimeInstanceId))
                    {
                        continue;
                    }

                    Assert.DoesNotContain(
                        forbiddenPrefixes,
                        prefix => run.RuntimeInstanceId!.Contains(prefix, StringComparison.Ordinal));
                }
            }
        }

        /// <summary>
        /// Gets the runtime prefixes allowed for a tenant in a production scenario.
        /// </summary>
        /// <param name="scenario">The production scenario definition.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <returns>The allowed runtime instance identifier prefixes.</returns>
        private static IReadOnlyList<string> GetAllowedRuntimePrefixes(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionTenantScenarioDefinition tenant)
        {
            var prefixes = new List<string>
            {
                tenant.RuntimeInstanceIdPrefix
            };

            if (IsHybridTenant(tenant))
            {
                prefixes.AddRange(
                    scenario.Tenants
                        .Where(IsSharedTenant)
                        .Select(x => x.RuntimeInstanceIdPrefix));
            }

            return prefixes
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>
        /// Determines whether a tenant scenario represents a hybrid tenant.
        /// </summary>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <returns><c>true</c> when the tenant is hybrid; otherwise, <c>false</c>.</returns>
        private static bool IsHybridTenant(
            ProductionTenantScenarioDefinition tenant)
        {
            return tenant.TenantId.Contains("hybrid", StringComparison.OrdinalIgnoreCase) ||
                   tenant.RuntimeInstanceIdPrefix.Contains("hybrid", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a tenant scenario represents shared runtime capacity.
        /// </summary>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <returns><c>true</c> when the tenant represents shared runtime capacity; otherwise, <c>false</c>.</returns>
        private static bool IsSharedTenant(
            ProductionTenantScenarioDefinition tenant)
        {
            return tenant.TenantId.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
                   tenant.RuntimeInstanceIdPrefix.Contains("shared", StringComparison.OrdinalIgnoreCase) ||
                   !tenant.ExpectDedicatedRuntimePrefix;
        }
    }
}