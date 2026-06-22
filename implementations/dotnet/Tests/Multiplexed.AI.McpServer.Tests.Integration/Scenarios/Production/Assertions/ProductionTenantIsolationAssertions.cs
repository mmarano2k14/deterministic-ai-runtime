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
        /// Verifies that tenant runs were assigned only to runtime instances matching the tenant runtime prefix.
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
                var tenantResult =
                    result.Tenants.Single(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.NotEmpty(
                    tenantResult.RuntimeInstanceIds);

                Assert.All(
                    tenantResult.RuntimeInstanceIds,
                    runtimeInstanceId =>
                    {
                        Assert.Contains(
                            tenant.RuntimeInstanceIdPrefix,
                            runtimeInstanceId,
                            StringComparison.Ordinal);
                    });

                Assert.All(
                    tenantResult.Runs,
                    run =>
                    {
                        Assert.False(
                            string.IsNullOrWhiteSpace(run.RuntimeInstanceId));

                        Assert.Contains(
                            tenant.RuntimeInstanceIdPrefix,
                            run.RuntimeInstanceId!,
                            StringComparison.Ordinal);
                    });
            }
        }

        /// <summary>
        /// Verifies that one tenant did not use another tenant runtime prefix.
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
                var otherPrefixes =
                    scenario.Tenants
                        .Where(x => !string.Equals(x.TenantId, tenant.TenantId, StringComparison.Ordinal))
                        .Select(x => x.RuntimeInstanceIdPrefix)
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
                        otherPrefixes,
                        prefix => runtimeInstanceId.Contains(prefix, StringComparison.Ordinal));
                }
            }
        }
    }
}