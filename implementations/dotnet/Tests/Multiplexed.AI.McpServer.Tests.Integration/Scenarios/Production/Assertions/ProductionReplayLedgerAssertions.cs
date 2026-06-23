using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions
{
    /// <summary>
    /// Provides assertions for replay, ledger, and trace visibility in production runtime scenarios.
    /// </summary>
    /// <remarks>
    /// These assertions validate the observable execution data produced by the runtime.
    /// In process-host scenarios, this data must cross process boundaries through durable
    /// stores such as MongoDB-backed decision ledger, replay metadata, and runtime trace stores.
    /// </remarks>
    public static class ProductionReplayLedgerAssertions
    {
        /// <summary>
        /// Asserts that replay, ledger, and trace data are available according to the scenario assertion options.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="result">The production runtime scenario result.</param>
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

            foreach (var tenant in result.Tenants)
            {
                foreach (var run in tenant.Runs)
                {
                    AssertRunReplayLedgerTraceAvailable(
                        scenario,
                        tenant,
                        run);
                }
            }
        }

        /// <summary>
        /// Asserts replay, ledger, and trace data for one completed production run.
        /// </summary>
        /// <param name="scenario">The production runtime scenario definition.</param>
        /// <param name="tenant">The tenant scenario result.</param>
        /// <param name="run">The run scenario result.</param>
        private static void AssertRunReplayLedgerTraceAvailable(
            ProductionRuntimeScenarioDefinition scenario,
            ProductionTenantScenarioResult tenant,
            ProductionRunScenarioResult run)
        {
            if (scenario.Assertions.AssertLedger)
            {
                Assert.True(
                    run.HasLedger,
                    $"Expected ledger entries for execution '{run.ExecutionId}'. TenantId='{tenant.TenantId}', RuntimeInstanceId='{run.RuntimeInstanceId}', SharedRunId='{run.SharedRunId}'.");
            }

            if (scenario.Assertions.AssertTrace)
            {
                Assert.True(
                    run.HasTrace,
                    $"Expected trace events for execution '{run.ExecutionId}'. TenantId='{tenant.TenantId}', RuntimeInstanceId='{run.RuntimeInstanceId}', SharedRunId='{run.SharedRunId}'.");
            }

            if (scenario.Assertions.AssertReplayReport)
            {
                Assert.True(
                    run.HasReplayReport,
                    $"Expected replay report for execution '{run.ExecutionId}'. TenantId='{tenant.TenantId}', RuntimeInstanceId='{run.RuntimeInstanceId}', SharedRunId='{run.SharedRunId}'.");
            }

            if (scenario.Assertions.AssertReplayLedger)
            {
                Assert.True(
                    run.HasReplayLedger,
                    $"Expected replay ledger for execution '{run.ExecutionId}'. TenantId='{tenant.TenantId}', RuntimeInstanceId='{run.RuntimeInstanceId}', SharedRunId='{run.SharedRunId}'.");
            }

            if (scenario.Assertions.AssertReplayTrace)
            {
                Assert.True(
                    run.HasReplayTrace,
                    $"Expected replay trace timeline for execution '{run.ExecutionId}'. TenantId='{tenant.TenantId}', RuntimeInstanceId='{run.RuntimeInstanceId}', SharedRunId='{run.SharedRunId}'.");
            }
        }
    }
}