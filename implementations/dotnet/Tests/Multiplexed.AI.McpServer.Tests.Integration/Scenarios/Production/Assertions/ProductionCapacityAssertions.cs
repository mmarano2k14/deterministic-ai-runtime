using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains common capacity and scale-out assertions for production runtime scenarios.
    /// </summary>
    public static class ProductionCapacityAssertions
    {
        /// <summary>
        /// Verifies that each tenant respected its maximum runtime instance limit.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertMaxRuntimeInstancesWereRespected(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants)
            {
                var tenantResult =
                    result.Tenants.Single(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.True(
                    tenantResult.RuntimeInstanceIds.Count <= tenant.MaxRuntimeInstances,
                    $"Tenant '{tenant.TenantId}' exceeded MaxRuntimeInstances. " +
                    $"ExpectedMax='{tenant.MaxRuntimeInstances}', " +
                    $"Actual='{tenantResult.RuntimeInstanceIds.Count}', " +
                    $"RuntimeInstances='{string.Join(", ", tenantResult.RuntimeInstanceIds)}'.");
            }
        }

        /// <summary>
        /// Verifies that expected capacity overflow was observed when configured.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertExpectedCapacityOverflowWasObserved(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenant in scenario.Tenants.Where(x => x.ExpectCapacityOverflow))
            {
                var tenantResult =
                    result.Tenants.Single(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.True(
                    tenantResult.CapacityOverflowObserved,
                    $"Expected capacity overflow for tenant '{tenant.TenantId}', but no overflow was observed.");
            }
        }

        /// <summary>
        /// Verifies that fulfilled scale-out requests contain fulfilled runtime instance ids.
        /// </summary>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertFulfilledScaleOutRequestsHaveRuntimeInstanceIds(
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenantResult in result.Tenants)
            {
                foreach (var request in tenantResult.ScaleOutRequests.Where(x =>
                             string.Equals(
                                 x.Status,
                                 "Fulfilled",
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId),
                        $"Scale-out request '{request.RequestId}' for tenant '{tenantResult.TenantId}' was fulfilled without a runtime instance id.");
                }
            }
        }
    }
}