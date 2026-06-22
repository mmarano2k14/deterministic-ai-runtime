using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http
{
    /// <summary>
    /// Contains production-grade HTTP process-host runtime scenario tests.
    /// </summary>
    public sealed class HttpProcessHostProductionScenarioTests
    {
        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostProductionScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostProductionScenarioTests(
            ITestOutputHelper output)
        {
            this.output =
                output;
        }

        /// <summary>
        /// Verifies that the HTTP provider with process-based host creation can execute
        /// the reusable production multi-tenant capacity, replay, ledger, and trace scenario.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Run_MultiTenant_Capacity_Replay_Ledger_Production_Scenario()
        {
            var scenario =
                ProductionRuntimeScenarioFactory.CreateMultiTenantCapacityReplayLedgerScenario();

            var runner =
                new HttpProcessHostProductionScenarioRunner(
                    this.output);

            var result =
                await runner
                    .RunAsync(scenario)
                    .ConfigureAwait(false);

            ProductionRuntimeScenarioAssertions.AssertScenarioShape(
                scenario,
                result);

            ProductionRuntimeScenarioAssertions.AssertAllRunsCompleted(
                scenario,
                result);

            ProductionCapacityAssertions.AssertMaxRuntimeInstancesWereRespected(
                scenario,
                result);

            ProductionCapacityAssertions.AssertFulfilledScaleOutRequestsHaveRuntimeInstanceIds(
                result);

            ProductionTenantIsolationAssertions.AssertTenantRuntimePrefixesWereRespected(
                scenario,
                result);

            ProductionTenantIsolationAssertions.AssertNoCrossTenantRuntimePrefixUsage(
                scenario,
                result);

            ProductionReplayLedgerAssertions.AssertReplayLedgerTraceAvailable(
                scenario,
                result);
        }
    }
}