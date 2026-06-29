using ModelContextProtocol.Protocol;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
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
        /// Verifies that recovered executions can be replayed and inspected through MCP.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="finalStatuses">The terminal runtime run statuses.</param>
        /// <returns>The recovered execution replay proof records.</returns>
        public static async Task<IReadOnlyCollection<RecoveredExecutionReplayProofRecord>> AssertRecoveredExecutionsReplayableThroughMcpAsync(
            McpTestClient mcp,
            string tenantId,
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> finalStatuses, string requestedBy = "", string source = "")
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(finalStatuses);

            var results =
                new List<RecoveredExecutionReplayProofRecord>();

            foreach (var status in finalStatuses)
            {
                var executionId =
                    status.ExecutionId ??
                    status.RunState?.ExecutionId;

                Assert.False(
                    string.IsNullOrWhiteSpace(executionId),
                    $"Recovered runtime status did not expose an execution id. TenantId='{tenantId}', RuntimeInstanceId='{status.RuntimeInstanceId}', RunId='{status.RunId}', Status='{status.RunState?.Status}'.");

                var replayRequest =
                    new AiReplayControlRequest
                    {
                        ExecutionId = executionId!,
                        CorrelationId = $"recovered-execution-replay-{Guid.NewGuid():N}",
                        RequestedBy = requestedBy,
                        Source = source,
                        Operation = AiReplayOperation.Replay
                    };

                var replayResult =
                    await mcp.ReplayExecutionAsync(replayRequest)
                        .ConfigureAwait(false);

                Assert.True(
                    replayResult.Success,
                    replayResult.FailureReason ?? replayResult.Message);

                replayRequest.Operation =
                    AiReplayOperation.GetReport;

                var replayReport =
                    await mcp.GetReplayReportAsync(replayRequest)
                        .ConfigureAwait(false);

                Assert.True(
                    replayReport.Success,
                    replayReport.FailureReason ?? replayReport.Message);

                replayRequest.Operation =
                    AiReplayOperation.GetLedger;

                var replayLedger =
                    await mcp.GetReplayLedgerAsync(replayRequest)
                        .ConfigureAwait(false);

                Assert.True(
                    replayLedger.Success,
                    replayLedger.FailureReason ?? replayLedger.Message);

                replayRequest.Operation =
                    AiReplayOperation.GetTimeline;

                var replayTrace =
                    await mcp.GetReplayTraceAsync(replayRequest)
                        .ConfigureAwait(false);

                Assert.True(
                    replayTrace.Success,
                    replayTrace.FailureReason ?? replayTrace.Message);

                results.Add(
                    new RecoveredExecutionReplayProofRecord
                    {
                        TenantId = tenantId,
                        RuntimeInstanceId = status.RuntimeInstanceId,
                        LocalRunId = status.RunId,
                        ExecutionId = executionId!,
                        ReplaySucceeded = replayResult.Success,
                        ReplayReportAvailable = replayReport.Success,
                        ReplayLedgerAvailable = replayLedger.Success,
                        ReplayTraceAvailable = replayTrace.Success
                    });
            }

            return results;
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