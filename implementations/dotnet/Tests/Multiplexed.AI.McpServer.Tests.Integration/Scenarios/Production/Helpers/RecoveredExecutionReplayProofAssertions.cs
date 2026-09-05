using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

using Multiplexed.AI.Runtime.Observability.Performance;
namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers
{
    /// <summary>
    /// Provides transport-neutral MCP replay, ledger, and trace assertions for completed runtime executions.
    /// </summary>
    public static class RecoveredExecutionReplayProofAssertions
    {
        /// <summary>
        /// Verifies that completed runtime executions remain replayable and expose ledger and trace evidence through MCP.
        /// </summary>
        public static async Task<IReadOnlyCollection<RecoveredExecutionReplayProofRecord>> AssertRecoveredExecutionsReplayableThroughMcpAsync(
            McpTestClient mcp,
            string tenantId,
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> finalStatuses,
            string requestedBy,
            string source,
            Action<string, int, TimeSpan>? onBackpressureRetry = null)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(finalStatuses);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            using var perf2MongoAuditAttribution =
                AiMongoAttributionDiagnostics.OverrideForTestHarnessAudit();

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
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.ReplayExecutionAsync(replayRequest),
                            $"replay.execute:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                var replayFailureReason =
                    replayResult.FailureReason ??
                    replayResult.Message;

                var isSyntheticRecoveredExecution =
                    executionId.StartsWith(
                        "http-runtime-inventory-running-execution-",
                        StringComparison.Ordinal);

                /*
                if (!isSyntheticRecoveredExecution)
                {
                    Assert.True(
                        replayResult.Success,
                        replayFailureReason);
                }
                */


                Assert.True(
                    replayResult.Success,
                    $"Recovered execution is not replayable through MCP. TenantId='{tenantId}', ExecutionId='{executionId}', RuntimeInstanceId='{status.RuntimeInstanceId}', RunId='{status.RunId}', Failure='{replayFailureReason}'.");

                replayRequest.Operation =
                    AiReplayOperation.GetReport;

                var replayReport =
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.GetReplayReportAsync(replayRequest),
                            $"replay.report:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                replayRequest.Operation =
                    AiReplayOperation.GetLedger;

                var replayLedger =
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.GetReplayLedgerAsync(replayRequest),
                            $"replay.ledger:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                replayRequest.Operation =
                    AiReplayOperation.GetTimeline;

                var replayTrace =
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.GetReplayTraceAsync(replayRequest),
                            $"replay.trace:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                var executionLedger =
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.GetLedgerByExecutionAsync(executionId!),
                            $"observability.execution-ledger:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                Assert.NotEmpty(executionLedger);

                var hasCompletionEvidence =
                    executionLedger.Any(entry =>
                        string.Equals(entry.EventType, AiEngineEvents.Execution.Completed, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(entry.EventType, AiEngineEvents.Finalization.Completed, StringComparison.OrdinalIgnoreCase));

                var hasStepCompletionEvidence =
                    executionLedger.Any(entry =>
                        string.Equals(entry.EventType, AiEngineEvents.Step.Completed, StringComparison.OrdinalIgnoreCase));

                Assert.True(
                    hasCompletionEvidence,
                    $"Recovered execution has no completion ledger evidence. TenantId='{tenantId}', ExecutionId='{executionId}'.");

                Assert.True(
                    hasStepCompletionEvidence,
                    $"Recovered execution has no step completion ledger evidence. TenantId='{tenantId}', ExecutionId='{executionId}'.");

                var executionTrace =
                    await McpBackpressureRetryHelper
                        .ExecuteAsync(
                            () => mcp.GetTraceByExecutionAsync(executionId!),
                            $"observability.execution-trace:{tenantId}:{executionId}",
                            onRetry: onBackpressureRetry)
                        .ConfigureAwait(false);

                Assert.NotEmpty(executionTrace);

                results.Add(
                    new RecoveredExecutionReplayProofRecord
                    {
                        TenantId = tenantId,
                        RuntimeInstanceId = status.RuntimeInstanceId,
                        LocalRunId = status.RunId,
                        ExecutionId = executionId!,
                        ReplaySucceeded = replayResult.Success,
                        ReplayFailureReason = replayFailureReason,
                        SyntheticRecoveredExecution = isSyntheticRecoveredExecution,
                        ReplayReportAvailable = replayReport.Success,
                        ReplayLedgerAvailable = replayLedger.Success,
                        ReplayTraceAvailable = replayTrace.Success,
                        ExecutionLedgerAvailable = executionLedger.Count > 0,
                        ExecutionTraceAvailable = executionTrace.Count > 0,
                        CompletionLedgerEvidenceAvailable = hasCompletionEvidence,
                        StepCompletionLedgerEvidenceAvailable = hasStepCompletionEvidence
                    });
            }

            return results;
        }
    }
}
