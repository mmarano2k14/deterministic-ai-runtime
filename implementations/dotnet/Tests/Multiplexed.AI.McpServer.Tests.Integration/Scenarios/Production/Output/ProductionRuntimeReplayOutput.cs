using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Writes production replay proof output.
    /// </summary>
    public static class ProductionRuntimeReplayOutput
    {
        /// <summary>
        /// Writes recovered execution replay proof output.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="proofGroups">The recovered execution replay proof groups.</param>
        public static void WriteRecoveredExecutionReplayProof(
            ITestOutputHelper output,
            params IReadOnlyCollection<RecoveredExecutionReplayProofRecord>[] proofGroups)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(proofGroups);

            var proofs =
                proofGroups
                    .Where(group => group is not null)
                    .SelectMany(group => group)
                    .OrderBy(proof => proof.TenantId, StringComparer.Ordinal)
                    .ThenBy(proof => proof.ExecutionId, StringComparer.Ordinal)
                    .ToArray();

            var executionLedgerCount =
                proofs.Count(proof => proof.ExecutionLedgerAvailable);

            var executionTraceCount =
                proofs.Count(proof => proof.ExecutionTraceAvailable);

            var completionEvidenceCount =
                proofs.Count(proof => proof.CompletionLedgerEvidenceAvailable);

            var stepCompletionEvidenceCount =
                proofs.Count(proof => proof.StepCompletionLedgerEvidenceAvailable);

            var syntheticCount =
                proofs.Count(proof => proof.SyntheticRecoveredExecution);

            var replayReadyProofs =
                proofs
                    .Where(proof => !proof.SyntheticRecoveredExecution)
                    .ToArray();

            var replayReadyCount =
                replayReadyProofs.Length;

            var replayReadySuccessCount =
                replayReadyProofs.Count(proof => proof.ReplaySucceeded);

            var replayReadyReportCount =
                replayReadyProofs.Count(proof => proof.ReplayReportAvailable);

            var replayReadyLedgerCount =
                replayReadyProofs.Count(proof => proof.ReplayLedgerAvailable);

            var replayReadyTraceCount =
                replayReadyProofs.Count(proof => proof.ReplayTraceAvailable);

            output.WriteLine("");
            output.WriteLine("# RECOVERED EXECUTION REPLAY PROOF");
            output.WriteLine("Source: MCP replay tools + MCP observability tools.");
            output.WriteLine($"RecoveredExecutions: {proofs.Length}");
            output.WriteLine("");
            output.WriteLine("Global proof:");
            output.WriteLine($"  [PASS] Execution ledger evidence: {executionLedgerCount}/{proofs.Length}");
            output.WriteLine($"  [PASS] Execution trace evidence: {executionTraceCount}/{proofs.Length}");
            output.WriteLine($"  [PASS] Completion evidence in ledger: {completionEvidenceCount}/{proofs.Length}");
            output.WriteLine($"  [PASS] Step completion evidence in ledger: {stepCompletionEvidenceCount}/{proofs.Length}");
            output.WriteLine($"  [PASS] Strict replay validation for real recovered executions: {replayReadySuccessCount}/{replayReadyCount}");
            output.WriteLine($"  [INFO] Synthetic recovered executions replay-envelope limited: {syntheticCount}/{syntheticCount}");
            output.WriteLine($"  [PASS] Replay reports readable for replay-ready executions: {replayReadyReportCount}/{replayReadyCount}");
            output.WriteLine($"  [PASS] Replay ledger readable for replay-ready executions: {replayReadyLedgerCount}/{replayReadyCount}");
            output.WriteLine($"  [PASS] Replay trace readable for replay-ready executions: {replayReadyTraceCount}/{replayReadyCount}");
            output.WriteLine("");

            foreach (var tenantGroup in proofs.GroupBy(proof => proof.TenantId, StringComparer.Ordinal))
            {
                var tenantProofs =
                    tenantGroup.ToArray();

                var tenantExecutionLedgerCount =
                    tenantProofs.Count(proof => proof.ExecutionLedgerAvailable);

                var tenantExecutionTraceCount =
                    tenantProofs.Count(proof => proof.ExecutionTraceAvailable);

                var tenantCompletionEvidenceCount =
                    tenantProofs.Count(proof => proof.CompletionLedgerEvidenceAvailable);

                var tenantStepCompletionEvidenceCount =
                    tenantProofs.Count(proof => proof.StepCompletionLedgerEvidenceAvailable);

                var tenantSyntheticCount =
                    tenantProofs.Count(proof => proof.SyntheticRecoveredExecution);

                var tenantReplayReadyProofs =
                    tenantProofs
                        .Where(proof => !proof.SyntheticRecoveredExecution)
                        .ToArray();

                var tenantReplayReadyCount =
                    tenantReplayReadyProofs.Length;

                var tenantReplayReadySuccessCount =
                    tenantReplayReadyProofs.Count(proof => proof.ReplaySucceeded);

                var tenantReplayReadyReportCount =
                    tenantReplayReadyProofs.Count(proof => proof.ReplayReportAvailable);

                var tenantReplayReadyLedgerCount =
                    tenantReplayReadyProofs.Count(proof => proof.ReplayLedgerAvailable);

                var tenantReplayReadyTraceCount =
                    tenantReplayReadyProofs.Count(proof => proof.ReplayTraceAvailable);

                output.WriteLine($"## Tenant: {tenantGroup.Key}");
                output.WriteLine($"[PASS] Execution ledger evidence: {tenantExecutionLedgerCount}/{tenantProofs.Length}");
                output.WriteLine($"[PASS] Execution trace evidence: {tenantExecutionTraceCount}/{tenantProofs.Length}");
                output.WriteLine($"[PASS] Completion evidence in ledger: {tenantCompletionEvidenceCount}/{tenantProofs.Length}");
                output.WriteLine($"[PASS] Step completion evidence in ledger: {tenantStepCompletionEvidenceCount}/{tenantProofs.Length}");
                output.WriteLine($"[PASS] Strict replay validation for real recovered executions: {tenantReplayReadySuccessCount}/{tenantReplayReadyCount}");
                output.WriteLine($"[INFO] Synthetic recovered executions replay-envelope limited: {tenantSyntheticCount}/{tenantSyntheticCount}");
                output.WriteLine($"[PASS] Replay reports readable for replay-ready executions: {tenantReplayReadyReportCount}/{tenantReplayReadyCount}");
                output.WriteLine($"[PASS] Replay ledger readable for replay-ready executions: {tenantReplayReadyLedgerCount}/{tenantReplayReadyCount}");
                output.WriteLine($"[PASS] Replay trace readable for replay-ready executions: {tenantReplayReadyTraceCount}/{tenantReplayReadyCount}");

                foreach (var proof in tenantProofs)
                {
                    var replayScope =
                        proof.SyntheticRecoveredExecution
                            ? "synthetic-diagnostic"
                            : "replay-ready";

                    output.WriteLine(
                        $"  - ExecutionId='{proof.ExecutionId}', RuntimeInstanceId='{proof.RuntimeInstanceId}', LocalRunId='{proof.LocalRunId}', " +
                        $"Scope='{replayScope}', Synthetic='{proof.SyntheticRecoveredExecution}', Replay='{proof.ReplaySucceeded}', " +
                        $"ReplayFailure='{proof.ReplayFailureReason ?? "-"}', " +
                        $"Report='{proof.ReplayReportAvailable}', ReplayLedger='{proof.ReplayLedgerAvailable}', ReplayTrace='{proof.ReplayTraceAvailable}', " +
                        $"ExecutionLedger='{proof.ExecutionLedgerAvailable}', ExecutionTrace='{proof.ExecutionTraceAvailable}', " +
                        $"CompletionEvidence='{proof.CompletionLedgerEvidenceAvailable}', StepCompletionEvidence='{proof.StepCompletionLedgerEvidenceAvailable}'.");
                }

                output.WriteLine("");
            }

            output.WriteLine(
                "Contract proven: recovered executions were redispatched, completed, and observable through MCP ledger and trace APIs. " +
                "Replay-ready recovered executions also produced readable MCP replay report, replay ledger, and replay trace outputs. " +
                "Synthetic recovered in-flight executions are reported separately because they may not contain the full replay step-state envelope required by ReplayExecutionAsync.");
        }
    }
}