using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains common assertions for provider-agnostic production runtime scenarios.
    /// </summary>
    public static class ProductionRuntimeScenarioAssertions
    {
        /// <summary>
        /// Verifies that the scenario result matches the expected scenario shape.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertScenarioShape(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            Assert.Equal(
                scenario.Name,
                result.ScenarioName);

            Assert.False(
                string.IsNullOrWhiteSpace(result.ControlPlaneId));

            Assert.False(
                string.IsNullOrWhiteSpace(result.ProviderLabel));

            Assert.Equal(
                scenario.Tenants.Count,
                result.Tenants.Count);

            foreach (var tenant in scenario.Tenants)
            {
                var tenantResult =
                    result.Tenants.SingleOrDefault(x =>
                        string.Equals(
                            x.TenantId,
                            tenant.TenantId,
                            StringComparison.Ordinal));

                Assert.NotNull(tenantResult);

                Assert.Equal(
                    tenant.Run.RunCount,
                    tenantResult!.SharedRunIds.Count);

                Assert.Equal(
                    tenant.Run.RunCount,
                    tenantResult.Runs.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(tenantResult.PipelineKey));
            }
        }

        /// <summary>
        /// Verifies that all submitted runs completed successfully.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertAllRunsCompleted(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            foreach (var tenantResult in result.Tenants)
            {
                Assert.All(
                    tenantResult.Runs,
                    run =>
                    {
                        Assert.False(
                            string.IsNullOrWhiteSpace(run.SharedRunId));

                        Assert.False(
                            string.IsNullOrWhiteSpace(run.RuntimeInstanceId));

                        Assert.False(
                            string.IsNullOrWhiteSpace(run.LocalRunId));

                        Assert.False(
                            string.IsNullOrWhiteSpace(run.ExecutionId));

                        Assert.Equal(
                            "completed",
                            run.FinalStatus);
                    });
            }
        }
    }
}