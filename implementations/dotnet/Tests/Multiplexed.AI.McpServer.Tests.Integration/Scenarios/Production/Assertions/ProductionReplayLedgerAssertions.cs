using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Contains common replay, ledger, and trace assertions for production runtime scenarios.
    /// </summary>
    public static class ProductionReplayLedgerAssertions
    {
        /// <summary>
        /// Verifies that replay, ledger, and trace were available for all completed runs when required by the scenario.
        /// </summary>
        /// <param name="scenario">The expected scenario definition.</param>
        /// <param name="result">The actual scenario result.</param>
        public static void AssertReplayLedgerTraceAvailable(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionRuntimeScenarioResult result)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(result);

            if (!scenario.AssertReplayLedgerTrace)
            {
                return;
            }

            foreach (var tenantResult in result.Tenants)
            {
                Assert.All(
                    tenantResult.Runs,
                    run =>
                    {
                        Assert.False(
                            string.IsNullOrWhiteSpace(run.ExecutionId));

                        Assert.True(
                            run.HasLedger,
                            $"Expected ledger entries for execution '{run.ExecutionId}'.");

                        Assert.True(
                            run.HasTrace,
                            $"Expected trace events for execution '{run.ExecutionId}'.");

                        Assert.True(
                            run.HasReplayReport,
                            $"Expected replay report for execution '{run.ExecutionId}'.");

                        Assert.True(
                            run.HasReplayLedger,
                            $"Expected replay ledger for execution '{run.ExecutionId}'.");

                        Assert.True(
                            run.HasReplayTrace,
                            $"Expected replay trace for execution '{run.ExecutionId}'.");
                    });
            }
        }
    }
}