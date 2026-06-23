using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains assertions for tenant runtime mode propagation in production scenarios.
    /// </summary>
    public static class ProductionTenantRuntimeModeAssertions
    {
        /// <summary>
        /// Asserts that tenant runtime mode settings were propagated to scale-out requests.
        /// </summary>
        /// <param name="scenario">The scenario definition.</param>
        /// <param name="result">The scenario result.</param>
        public static void AssertTenantRuntimeModesWerePropagated(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants)
            {
                var tenantResult =
                    result.Tenants.Single(resultTenant =>
                        string.Equals(
                            resultTenant.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.NotEmpty(
                    tenantResult.ScaleOutRequests);

                var expectedIsolationMode =
                    ProductionTenantRuntimeModeMapper
                        .ResolveIsolationMode(tenant.RuntimeMode)
                        .ToString();

                var expectedPreferDedicatedCapacity =
                    ProductionTenantRuntimeModeMapper
                        .ResolvePreferDedicatedCapacity(tenant.RuntimeMode);

                var expectedAllowSharedFallback =
                    ProductionTenantRuntimeModeMapper
                        .ResolveAllowSharedFallback(tenant.RuntimeMode);

                Assert.All(
                    tenantResult.ScaleOutRequests,
                    request =>
                    {
                        Assert.Equal(
                            tenant.TenantId,
                            request.TenantId);

                        Assert.Equal(
                            tenant.TenantGroupId,
                            request.TenantGroupId);

                        Assert.Equal(
                            expectedIsolationMode,
                            request.IsolationMode);

                        Assert.Equal(
                            expectedPreferDedicatedCapacity,
                            request.PreferDedicatedCapacity);

                        Assert.Equal(
                            expectedAllowSharedFallback,
                            request.AllowSharedFallback);

                        Assert.Equal(
                            tenant.RuntimeInstanceIdPrefix,
                            request.RuntimeInstanceIdPrefix);

                        Assert.Equal(
                            tenant.WorkerCountPerInstance,
                            request.WorkerCountPerInstance);

                        Assert.Equal(
                            tenant.MaxConcurrentRunsPerInstance,
                            request.MaxConcurrentRunsPerInstance);

                        Assert.Equal(
                            tenant.LocalQueueCapacity,
                            request.LocalQueueCapacity);
                    });
            }
        }
    }
}