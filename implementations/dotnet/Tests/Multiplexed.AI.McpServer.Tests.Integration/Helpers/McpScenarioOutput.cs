using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Activity;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.McpServer.Models.Responses;
using Multiplexed.AI.McpServer.Tools;
using System.Text;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Provides reusable MCP scenario diagnostic output helpers.
    /// </summary>
    public static class McpScenarioOutput
    {
        public static void WriteSharedRunSummary(
            ITestOutputHelper output,
            string scenarioName,
            string pipelineName,
            string requestedSharedRunId,
            AiSharedRuntimeControllerResult submitResult,
            AiSharedRuntimeControllerResult listResult)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedSharedRunId);
            ArgumentNullException.ThrowIfNull(submitResult);
            ArgumentNullException.ThrowIfNull(listResult);

            var builder = new StringBuilder();

            builder.AppendLine("===========================================================");
            builder.AppendLine("MCP SHARED RUN SCENARIO SUMMARY");
            builder.AppendLine("===========================================================");
            builder.AppendLine($"Scenario              : {scenarioName}");
            builder.AppendLine($"PipelineName          : {pipelineName}");
            builder.AppendLine($"RequestedSharedRunId  : {requestedSharedRunId}");
            builder.AppendLine();

            AppendResult(builder, "SUBMIT RESULT", submitResult);
            AppendResult(builder, "LIST RESULT", listResult);

            builder.AppendLine("RUN DETAILS");
            builder.AppendLine("-----------------------------------------------------------");

            foreach (var run in listResult.Runs)
            {
                builder.AppendLine($"SharedRunId           : {run.SharedRunId}");
                builder.AppendLine($"Status                : {run.Status}");
                builder.AppendLine($"LocalRunId            : {run.LocalRunId}");
                builder.AppendLine($"ExecutionId           : {run.ExecutionId}");
                builder.AppendLine($"AssignedInstanceId    : {run.AssignedRuntimeInstanceId}");
                builder.AppendLine($"PipelineKey           : {run.PipelineKey}");
                builder.AppendLine($"TenantId              : {run.TenantId}");
                builder.AppendLine($"CorrelationId         : {run.CorrelationId}");
                builder.AppendLine($"RequestedBy           : {run.RequestedBy}");
                builder.AppendLine($"Source                : {run.Source}");
                builder.AppendLine($"SubmittedAtUtc        : {run.SubmittedAtUtc:O}");
                builder.AppendLine($"UpdatedAtUtc          : {run.UpdatedAtUtc:O}");
                builder.AppendLine("-----------------------------------------------------------");
            }

            builder.AppendLine("===========================================================");

            output.WriteLine(builder.ToString());
        }

        public static void WriteDrainSummary(
            ITestOutputHelper output,
            string scenarioName,
            string pipelineName,
            IReadOnlyList<AiSharedRuntimeControllerResult> submitResults,
            AiSharedRuntimeControllerResult beforeDrain,
            AiSharedQueuePumpResult drainResult,
            AiSharedRuntimeControllerResult afterDrain)
        {
            ArgumentNullException.ThrowIfNull(output);

            var builder = new StringBuilder();

            builder.AppendLine("===========================================================");
            builder.AppendLine("MCP DRAIN SCENARIO SUMMARY");
            builder.AppendLine("===========================================================");
            builder.AppendLine();

            builder.AppendLine($"Scenario              : {scenarioName}");
            builder.AppendLine($"PipelineName          : {pipelineName}");
            builder.AppendLine();

            builder.AppendLine("SUBMITTED RUNS");
            builder.AppendLine("-----------------------------------------------------------");
            builder.AppendLine($"Count                 : {submitResults.Count}");
            builder.AppendLine();

            foreach (var result in submitResults)
            {
                builder.AppendLine(
                    $"SharedRunId           : {result.SharedRunId}");
            }

            builder.AppendLine();

            builder.AppendLine("BEFORE DRAIN");
            builder.AppendLine("-----------------------------------------------------------");
            builder.AppendLine($"Success               : {beforeDrain.Success}");
            builder.AppendLine($"RunCount              : {beforeDrain.Runs.Count}");
            builder.AppendLine($"Message               : {beforeDrain.Message}");
            builder.AppendLine();

            builder.AppendLine("DRAIN RESULT");
            builder.AppendLine("-----------------------------------------------------------");
            builder.AppendLine($"Success               : {drainResult.Success}");
            builder.AppendLine($"RuntimeInstanceId     : {drainResult.RuntimeInstanceId}");
            builder.AppendLine($"AttemptedDispatches   : {drainResult.AttemptedDispatchCount}");
            builder.AppendLine($"SuccessfulDispatches  : {drainResult.SuccessfulDispatchCount}");
            builder.AppendLine($"FailedDispatches      : {drainResult.FailedDispatchCount}");
            builder.AppendLine($"StoppedBecauseNoItem  : {drainResult.StoppedBecauseNoItemAvailable}");
            builder.AppendLine($"FailureReason         : {drainResult.FailureReason}");
            builder.AppendLine($"DurationMs            : {drainResult.DurationMs}");
            builder.AppendLine();

            if (drainResult.Diagnostics.Count > 0)
            {
                builder.AppendLine("DRAIN DIAGNOSTICS");
                builder.AppendLine("-----------------------------------------------------------");

                foreach (var diagnostic in drainResult.Diagnostics)
                {
                    builder.AppendLine($"- {diagnostic}");
                }

                builder.AppendLine();
            }

            builder.AppendLine("AFTER DRAIN");
            builder.AppendLine("-----------------------------------------------------------");
            builder.AppendLine($"Success               : {afterDrain.Success}");
            builder.AppendLine($"RunCount              : {afterDrain.Runs.Count}");
            builder.AppendLine($"Message               : {afterDrain.Message}");
            builder.AppendLine();

            builder.AppendLine("RUN DETAILS");
            builder.AppendLine("-----------------------------------------------------------");

            foreach (var run in afterDrain.Runs.Where(run =>
                         string.Equals(
                             run.PipelineKey,
                             pipelineName,
                             StringComparison.Ordinal)))
            {
                builder.AppendLine($"SharedRunId           : {run.SharedRunId}");
                builder.AppendLine($"Status                : {run.Status}");
                builder.AppendLine($"AssignedInstanceId    : {run.AssignedRuntimeInstanceId}");
                builder.AppendLine($"LocalRunId            : {run.LocalRunId}");
                builder.AppendLine($"ExecutionId           : {run.ExecutionId}");
                builder.AppendLine($"PipelineKey           : {run.PipelineKey}");
                builder.AppendLine($"TenantId              : {run.TenantId}");
                builder.AppendLine($"CorrelationId         : {run.CorrelationId}");
                builder.AppendLine($"RequestedBy           : {run.RequestedBy}");
                builder.AppendLine($"Source                : {run.Source}");
                builder.AppendLine($"SubmittedAtUtc        : {run.SubmittedAtUtc:O}");
                builder.AppendLine($"UpdatedAtUtc          : {run.UpdatedAtUtc:O}");
                builder.AppendLine("-----------------------------------------------------------");
            }

            builder.AppendLine("===========================================================");

            output.WriteLine(builder.ToString());
        }

        public static void WriteRuntimeRunStatusSummary(
            ITestOutputHelper output,
            string scenarioName,
            string pipelineName,
            IReadOnlyList<AiSharedRunRecord> dispatchedRuns,
            IReadOnlyList<AiRuntimeQueueControlPlaneResult> runtimeStatuses)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(dispatchedRuns);
            ArgumentNullException.ThrowIfNull(runtimeStatuses);

            var builder = new StringBuilder();

            builder.AppendLine("===========================================================");
            builder.AppendLine("MCP RUNTIME RUN STATUS SUMMARY");
            builder.AppendLine("===========================================================");
            builder.AppendLine();

            builder.AppendLine($"Scenario              : {scenarioName}");
            builder.AppendLine($"PipelineName          : {pipelineName}");
            builder.AppendLine($"RunCount              : {dispatchedRuns.Count}");
            builder.AppendLine();

            builder.AppendLine("DISPATCHED RUNS");
            builder.AppendLine("-----------------------------------------------------------");

            foreach (var run in dispatchedRuns)
            {
                builder.AppendLine($"SharedRunId           : {run.SharedRunId}");
                builder.AppendLine($"Status                : {run.Status}");
                builder.AppendLine($"AssignedInstanceId    : {run.AssignedRuntimeInstanceId}");
                builder.AppendLine($"LocalRunId            : {run.LocalRunId}");
                builder.AppendLine($"ExecutionId           : {run.ExecutionId}");
                builder.AppendLine($"CorrelationId         : {run.CorrelationId}");
                builder.AppendLine($"SubmittedAtUtc        : {run.SubmittedAtUtc:O}");
                builder.AppendLine($"UpdatedAtUtc          : {run.UpdatedAtUtc:O}");
                builder.AppendLine("-----------------------------------------------------------");
            }

            builder.AppendLine();
            builder.AppendLine("RUNTIME STATUS");
            builder.AppendLine("-----------------------------------------------------------");

            foreach (var status in runtimeStatuses)
            {
                builder.AppendLine($"Operation             : {status.Operation}");
                builder.AppendLine($"Success               : {status.Success}");
                builder.AppendLine($"Message               : {status.Message}");
                builder.AppendLine($"RuntimeInstanceId     : {status.RuntimeInstanceId}");
                builder.AppendLine($"RunId                 : {status.RunId}");
                builder.AppendLine($"ExecutionId           : {status.ExecutionId}");
                builder.AppendLine($"Status                : {status.RunState?.Status}");
                builder.AppendLine($"StartedAtUtc          : {status.StartedAtUtc:O}");
                builder.AppendLine($"CompletedAtUtc        : {status.CompletedAtUtc:O}");
                builder.AppendLine($"DurationMs            : {status.DurationMs}");
                builder.AppendLine($"FailureReason         : {status.FailureReason}");

                if (status.Diagnostics.Count > 0)
                {
                    builder.AppendLine("Diagnostics:");

                    foreach (var diagnostic in status.Diagnostics)
                    {
                        builder.AppendLine($"  - {diagnostic}");
                    }
                }

                builder.AppendLine("-----------------------------------------------------------");
            }

            builder.AppendLine("===========================================================");

            output.WriteLine(
                builder.ToString());
        }

        private static void AppendResult(
            StringBuilder builder,
            string title,
            AiSharedRuntimeControllerResult result)
        {
            builder.AppendLine(title);
            builder.AppendLine("-----------------------------------------------------------");
            builder.AppendLine($"Operation             : {result.Operation}");
            builder.AppendLine($"Success               : {result.Success}");
            builder.AppendLine($"Message               : {result.Message}");
            builder.AppendLine($"SharedRunId           : {result.SharedRunId}");
            builder.AppendLine($"LocalRunId            : {result.LocalRunId}");
            builder.AppendLine($"ExecutionId           : {result.ExecutionId}");
            builder.AppendLine($"AssignedInstanceId    : {result.AssignedRuntimeInstanceId}");
            builder.AppendLine($"CorrelationId         : {result.CorrelationId}");
            builder.AppendLine($"RequestedBy           : {result.RequestedBy}");
            builder.AppendLine($"StartedAtUtc          : {result.StartedAtUtc:O}");
            builder.AppendLine($"CompletedAtUtc        : {result.CompletedAtUtc:O}");
            builder.AppendLine($"DurationMs            : {result.DurationMs}");
            builder.AppendLine($"FailureReason         : {result.FailureReason}");
            builder.AppendLine($"RunCount              : {result.Runs.Count}");
            builder.AppendLine();

            if (result.Diagnostics.Count > 0)
            {
                builder.AppendLine("Diagnostics");
                builder.AppendLine("-----------------------------------------------------------");

                foreach (var diagnostic in result.Diagnostics)
                {
                    builder.AppendLine($"- {diagnostic}");
                }

                builder.AppendLine();
            }
        }

        public static void WriteObservabilitySummary(
            ITestOutputHelper output,
            string scenarioName,
            string executionId,
            IReadOnlyList<AiDecisionLedgerEntry> ledgerEntries,
            IReadOnlyList<AiTraceEvent> traceEvents,
            string metricsStatus)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(ledgerEntries);
            ArgumentNullException.ThrowIfNull(traceEvents);

            output.WriteLine("===========================================================");
            output.WriteLine("MCP OBSERVABILITY SUMMARY");
            output.WriteLine("===========================================================");
            output.WriteLine($"Scenario              : {scenarioName}");
            output.WriteLine($"ExecutionId           : {executionId}");
            output.WriteLine("===========================================================");

            WriteLedgerSummary(
                output,
                ledgerEntries);

            WriteTraceSummary(
                output,
                traceEvents);

            WriteMetricsSummary(
                output,
                metricsStatus);

            output.WriteLine("===========================================================");
            output.WriteLine("OBSERVABILITY COUNTS");
            output.WriteLine("===========================================================");
            output.WriteLine($"Ledger Entries        : {ledgerEntries.Count}");
            output.WriteLine($"Trace Events          : {traceEvents.Count}");
            output.WriteLine($"Metrics Available     : {!string.IsNullOrWhiteSpace(metricsStatus)}");
            output.WriteLine("===========================================================");
        }

        public static void WriteLedgerSummary(
            ITestOutputHelper output,
            IReadOnlyList<AiDecisionLedgerEntry> ledgerEntries,
            int maxEntries = 500)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(ledgerEntries);

            output.WriteLine("");
            output.WriteLine("LEDGER SUMMARY");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"EntryCount            : {ledgerEntries.Count}");

            var byCategory = ledgerEntries
                .GroupBy(entry => entry.Category)
                .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                .ToArray();

            foreach (var group in byCategory)
            {
                output.WriteLine($"Category              : {group.Key} = {group.Count()}");
            }

            output.WriteLine("");
            output.WriteLine("LEDGER EVENTS");
            output.WriteLine("-----------------------------------------------------------");

            foreach (var entry in ledgerEntries
                .OrderBy(entry => entry.TimestampUtc)
                .Take(maxEntries))
            {
                output.WriteLine(
                    $"{entry.TimestampUtc:O} | {entry.Category} | {entry.EventType} | {entry.Outcome} | {entry.CorrelationContext.ExecutionId} | {entry.CorrelationContext.RuntimeInstanceId} | {entry.CorrelationContext.WorkerId}");
            }

            if (ledgerEntries.Count > maxEntries)
            {
                output.WriteLine($"... showing first {maxEntries} of {ledgerEntries.Count}");
            }
        }

        public static void WriteTraceSummary(
             ITestOutputHelper output,
             IReadOnlyList<AiTraceEvent> traceEvents,
             int maxEvents = 500)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(traceEvents);

            output.WriteLine("");
            output.WriteLine("TRACE SUMMARY");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"EventCount            : {traceEvents.Count}");

            var byCategory = traceEvents
                .GroupBy(traceEvent => traceEvent.Category ?? "unknown")
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();

            foreach (var group in byCategory)
            {
                output.WriteLine($"Category              : {group.Key} = {group.Count()}");
            }

            output.WriteLine("");
            output.WriteLine("TRACE EVENTS");
            output.WriteLine("-----------------------------------------------------------");

            foreach (var traceEvent in traceEvents
                .OrderBy(traceEvent => traceEvent.TimestampUtc)
                .Take(maxEvents))
            {
                output.WriteLine(
                    $"{traceEvent.TimestampUtc:O} | {traceEvent.Category} | {traceEvent.Name} | StepId={traceEvent.StepId ?? "-"} | ExecutionId={traceEvent.Correlation.Runtime?.ExecutionId ?? "-"} | RuntimeInstanceId={traceEvent.Correlation.Runtime?.RuntimeInstanceId ?? "-"} | WorkerId={traceEvent.Correlation.Runtime?.WorkerId ?? "-"}");
            }

            if (traceEvents.Count > maxEvents)
            {
                output.WriteLine($"... showing first {maxEvents} of {traceEvents.Count}");
            }
        }

        public static void WriteMetricsSummary(
            ITestOutputHelper output,
            string metricsStatus)
        {
            ArgumentNullException.ThrowIfNull(output);

            output.WriteLine("");
            output.WriteLine("METRICS SUMMARY");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine(string.IsNullOrWhiteSpace(metricsStatus)
                ? "Metrics status unavailable."
                : metricsStatus);
        }

        public static void WriteReplaySummary(
            ITestOutputHelper output,
            string scenarioName,
            string executionId,
            AiReplayControlResult replayResult,
            AiReplayControlResult replayReport,
            AiReplayControlResult replayLedger,
            AiReplayControlResult replayTrace)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(replayResult);
            ArgumentNullException.ThrowIfNull(replayReport);
            ArgumentNullException.ThrowIfNull(replayLedger);
            ArgumentNullException.ThrowIfNull(replayTrace);

            output.WriteLine("===========================================================");
            output.WriteLine("MCP REPLAY SUMMARY");
            output.WriteLine("===========================================================");
            output.WriteLine($"Scenario              : {scenarioName}");
            output.WriteLine($"ExecutionId           : {executionId}");
            output.WriteLine("===========================================================");

            WriteReplayControlResultHeader(
                output,
                "REPLAY EXECUTION",
                replayResult);

            WriteReplayControlResultHeader(
                output,
                "REPLAY REPORT",
                replayReport);

            if (replayReport.Report is not null)
            {
                WriteReplayReportSummary(
                    output,
                    replayReport);
            }

            WriteReplayControlResultHeader(
                output,
                "REPLAY LEDGER",
                replayLedger);

            WriteLedgerSummary(
                output,
                replayLedger.Ledger);

            WriteReplayControlResultHeader(
                output,
                "REPLAY TRACE",
                replayTrace);

            WriteTraceSummary(
                output,
                replayTrace.Timeline);

            output.WriteLine("===========================================================");
            output.WriteLine("REPLAY COUNTS");
            output.WriteLine("===========================================================");
            output.WriteLine($"Replay Success        : {replayResult.Success}");
            output.WriteLine($"Report Success        : {replayReport.Success}");
            output.WriteLine($"Ledger Entries        : {replayLedger.Ledger.Count}");
            output.WriteLine($"Trace Events          : {replayTrace.Timeline.Count}");
            output.WriteLine("===========================================================");
        }

        private static void WriteReplayControlResultHeader(
            ITestOutputHelper output,
            string title,
            AiReplayControlResult result)
        {
            output.WriteLine("");
            output.WriteLine(title);
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"Operation             : {result.Operation}");
            output.WriteLine($"Success               : {result.Success}");
            output.WriteLine($"Message               : {result.Message}");
            output.WriteLine($"ExecutionId           : {result.ExecutionId}");
            output.WriteLine($"CorrelationId         : {result.CorrelationId}");
            output.WriteLine($"RequestedBy           : {result.RequestedBy}");
            output.WriteLine($"StartedAtUtc          : {result.StartedAtUtc:O}");
            output.WriteLine($"CompletedAtUtc        : {result.CompletedAtUtc:O}");
            output.WriteLine($"DurationMs            : {result.DurationMs}");
            output.WriteLine($"FailureReason         : {result.FailureReason}");

            if (result.Diagnostics.Count > 0)
            {
                output.WriteLine("Diagnostics:");

                foreach (var diagnostic in result.Diagnostics)
                {
                    output.WriteLine($"  - {diagnostic}");
                }
            }
        }

        private static void WriteReplayReportSummary(
            ITestOutputHelper output,
            AiReplayControlResult result)
        {
            var report = result.Report;

            if (report is null)
            {
                return;
            }

            output.WriteLine("");
            output.WriteLine("REPLAY REPORT DETAILS");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"ReplayValid           : {report.ReplayValid}");
            output.WriteLine($"ExecutionFound        : {report.ExecutionFound}");
            output.WriteLine($"SnapshotFound         : {report.SnapshotFound}");
            output.WriteLine($"FingerprintFound      : {report.FingerprintFound}");
            output.WriteLine($"FingerprintMatches    : {report.FingerprintMatches}");
            output.WriteLine($"DependencyGraphValid  : {report.DependencyGraphValid}");
            output.WriteLine($"StepStateValid        : {report.StepStateValid}");
            output.WriteLine($"PayloadReferencesValid: {report.PayloadReferencesValid}");
            output.WriteLine($"IssueCount            : {report.Issues.Count}");

            if (report.Issues.Count > 0)
            {
                output.WriteLine("");
                output.WriteLine("REPLAY ISSUES");
                output.WriteLine("-----------------------------------------------------------");

                foreach (var issue in report.Issues.Take(30))
                {
                    output.WriteLine($"- {issue}");
                }

                if (report.Issues.Count > 30)
                {
                    output.WriteLine($"... showing first 30 of {report.Issues.Count}");
                }
            }
        }

        public static void WriteExecutionControlSummary(
    ITestOutputHelper output,
    string scenarioName,
    string executionId,
    AiExecutionControlPlaneResult pauseResult,
    AiExecutionControlPlaneResult pausedStatus,
    AiExecutionControlPlaneResult? resumeResult,
    AiExecutionControlPlaneResult? resumedStatus)
        {
            ArgumentNullException.ThrowIfNull(output);

            output.WriteLine("===========================================================");
            output.WriteLine("MCP EXECUTION CONTROL SUMMARY");
            output.WriteLine("===========================================================");
            output.WriteLine($"Scenario              : {scenarioName}");
            output.WriteLine($"ExecutionId           : {executionId}");
            output.WriteLine("===========================================================");

            WriteExecutionControlResult(
                output,
                "PAUSE RESULT",
                pauseResult);

            WriteExecutionControlResult(
                output,
                "STATUS AFTER PAUSE",
                pausedStatus);

            if (resumeResult is not null)
            {
                WriteExecutionControlResult(
                    output,
                    "RESUME RESULT",
                    resumeResult);
            }

            if (resumedStatus is not null)
            {
                WriteExecutionControlResult(
                    output,
                    "STATUS AFTER RESUME",
                    resumedStatus);
            }

            output.WriteLine("===========================================================");
        }

        private static void WriteExecutionControlResult(
            ITestOutputHelper output,
            string title,
            AiExecutionControlPlaneResult result)
        {
            output.WriteLine("");
            output.WriteLine(title);
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"Operation             : {result.Operation}");
            output.WriteLine($"Success               : {result.Success}");
            output.WriteLine($"Message               : {result.Message}");
            output.WriteLine($"ExecutionId           : {result.ExecutionId}");
            output.WriteLine($"ControlStatus         : {result.State?.Status}");
            output.WriteLine($"RequestedBy           : {result.RequestedBy}");
            output.WriteLine($"CorrelationId         : {result.CorrelationId}");
            output.WriteLine($"StartedAtUtc          : {result.StartedAtUtc:O}");
            output.WriteLine($"CompletedAtUtc        : {result.CompletedAtUtc:O}");
            output.WriteLine($"DurationMs            : {result.DurationMs}");
            output.WriteLine($"FailureReason         : {result.FailureReason}");

            if (result.Diagnostics.Count > 0)
            {
                output.WriteLine("Diagnostics:");

                foreach (var diagnostic in result.Diagnostics)
                {
                    output.WriteLine($"  - {diagnostic}");
                }
            }
        }

        public static void WriteSharedQueueSummary(
            ITestOutputHelper output,
            string scenarioName,
            string pipelineName,
            IReadOnlyList<AiSharedQueueItem> queueItems,
            SharedQueueStatusResult status)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentNullException.ThrowIfNull(queueItems);
            ArgumentNullException.ThrowIfNull(status);

            output.WriteLine("===========================================================");
            output.WriteLine("MCP SHARED QUEUE SUMMARY");
            output.WriteLine("===========================================================");
            output.WriteLine($"Scenario              : {scenarioName}");
            output.WriteLine($"PipelineName          : {pipelineName}");
            output.WriteLine("===========================================================");

            output.WriteLine("");
            output.WriteLine("QUEUE STATUS");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"TotalCount            : {status.TotalCount}");
            output.WriteLine($"PendingCount          : {status.PendingCount}");
            output.WriteLine($"ClaimedCount          : {status.ClaimedCount}");
            output.WriteLine($"DispatchedCount       : {status.DispatchedCount}");
            output.WriteLine($"CompletedCount        : {status.CompletedCount}");
            output.WriteLine($"FailedCount           : {status.FailedCount}");
            output.WriteLine($"CancelledCount        : {status.CancelledCount}");
            output.WriteLine($"OldestPendingAtUtc    : {status.OldestPendingAtUtc:O}");
            output.WriteLine($"NewestPendingAtUtc    : {status.NewestPendingAtUtc:O}");
            output.WriteLine($"IncludeTerminal       : {status.IncludeTerminal}");

            var scenarioItems = queueItems
                .Where(item => string.Equals(item.PipelineKey, pipelineName, StringComparison.Ordinal))
                .OrderBy(item => item.EnqueuedAtUtc)
                .ToArray();

            output.WriteLine("");
            output.WriteLine("SCENARIO QUEUE ITEMS");
            output.WriteLine("-----------------------------------------------------------");
            output.WriteLine($"ScenarioItemCount     : {scenarioItems.Length}");

            foreach (var item in scenarioItems)
            {
                output.WriteLine($"SharedRunId           : {item.SharedRunId}");
                output.WriteLine($"Status                : {item.Status}");
                output.WriteLine($"PipelineKey           : {item.PipelineKey}");
                output.WriteLine($"TenantId              : {item.TenantId}");
                output.WriteLine($"Priority              : {item.Priority}");
                output.WriteLine($"ClaimedByInstanceId   : {item.ClaimedByRuntimeInstanceId}");
                output.WriteLine($"ClaimedByWorkerId     : {item.ClaimedByWorkerId}");
                output.WriteLine($"ClaimToken            : {item.ClaimToken}");
                output.WriteLine($"EnqueuedAtUtc         : {item.EnqueuedAtUtc:O}");
                output.WriteLine($"UpdatedAtUtc          : {item.UpdatedAtUtc:O}");
                output.WriteLine($"ClaimedAtUtc          : {item.ClaimedAtUtc:O}");
                output.WriteLine($"ClaimExpiresAtUtc     : {item.ClaimExpiresAtUtc:O}");
                output.WriteLine("-----------------------------------------------------------");
            }

            output.WriteLine("===========================================================");
        }

        public static void WriteRuntimeInstanceSummary(
            ITestOutputHelper output,
            string scenarioName,
            IReadOnlyList<AiRuntimeInstanceSnapshot> allInstances,
            IReadOnlyList<AiRuntimeInstanceSnapshot> activeInstances,
            AiRuntimeInstanceSnapshot? selectedStatus)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentNullException.ThrowIfNull(allInstances);
            ArgumentNullException.ThrowIfNull(activeInstances);

            output.WriteLine("===========================================================");
            output.WriteLine("MCP RUNTIME INSTANCE SUMMARY");
            output.WriteLine("===========================================================");
            output.WriteLine($"Scenario              : {scenarioName}");
            output.WriteLine($"TotalInstances         : {allInstances.Count}");
            output.WriteLine($"ActiveInstances        : {activeInstances.Count}");
            output.WriteLine("===========================================================");

            if (selectedStatus is not null)
            {
                output.WriteLine("");
                output.WriteLine("SELECTED INSTANCE STATUS");
                output.WriteLine("-----------------------------------------------------------");

                WriteRuntimeInstance(
                    output,
                    selectedStatus);
            }
            else
            {
                output.WriteLine("");
                output.WriteLine("SELECTED INSTANCE STATUS");
                output.WriteLine("-----------------------------------------------------------");
                output.WriteLine("No runtime instance is currently registered.");
            }

            output.WriteLine("");
            output.WriteLine("ACTIVE INSTANCES");
            output.WriteLine("-----------------------------------------------------------");

            if (activeInstances.Count == 0)
            {
                output.WriteLine("No active runtime instance is currently registered.");
            }

            foreach (var instance in activeInstances)
            {
                WriteRuntimeInstance(
                    output,
                    instance);

                output.WriteLine("-----------------------------------------------------------");
            }

            output.WriteLine("===========================================================");
        }

        private static void WriteRuntimeInstance(
            ITestOutputHelper output,
            AiRuntimeInstanceSnapshot instance)
        {
            output.WriteLine($"RuntimeInstanceId     : {instance.RuntimeInstanceId}");
            output.WriteLine($"Status                : {instance.Status}");
            output.WriteLine($"HostName              : {instance.HostName}");
            output.WriteLine($"ProcessId             : {instance.ProcessId}");
            output.WriteLine($"KubernetesNamespace   : {instance.KubernetesNamespace}");
            output.WriteLine($"KubernetesPodName     : {instance.KubernetesPodName}");
            output.WriteLine($"KubernetesNodeName    : {instance.KubernetesNodeName}");
            output.WriteLine($"WorkerCount           : {instance.WorkerCount}");
            output.WriteLine($"QueuedRunCount        : {instance.QueuedRunCount}");
            output.WriteLine($"RunningRunCount       : {instance.RunningRunCount}");
            output.WriteLine($"ActiveRunCount        : {instance.ActiveRunCount}");
            output.WriteLine($"QueueCapacity         : {instance.QueueCapacity}");
            output.WriteLine($"MaxConcurrentRuns     : {instance.MaxConcurrentRuns}");
            output.WriteLine($"AvailableRunSlots     : {instance.AvailableRunSlots}");
            output.WriteLine($"IsQueuePaused         : {instance.IsQueuePaused}");
            output.WriteLine($"CanAcceptRun          : {instance.CanAcceptRun}");
            output.WriteLine($"RegisteredAtUtc       : {instance.RegisteredAtUtc:O}");
            output.WriteLine($"LastHeartbeatAtUtc    : {instance.LastHeartbeatAtUtc:O}");
            output.WriteLine($"SnapshotAtUtc         : {instance.SnapshotAtUtc:O}");
            output.WriteLine($"RuntimeVersion        : {instance.RuntimeVersion}");

            if (instance.Metadata.Count > 0)
            {
                output.WriteLine("Metadata:");

                foreach (var item in instance.Metadata)
                {
                    output.WriteLine($"  {item.Key}: {item.Value}");
                }
            }
        }

        public static void WriteSharedQueueActivitySummary(
    ITestOutputHelper output,
    string scenarioName,
    string pipelineName,
    AiSharedQueueActivityResult activity)
        {
            output.WriteLine(
                "===========================================================");

            output.WriteLine(
                "MCP SHARED QUEUE ACTIVITY SUMMARY");

            output.WriteLine(
                "===========================================================");

            output.WriteLine(
                $"Scenario              : {scenarioName}");

            output.WriteLine(
                $"PipelineName          : {pipelineName}");

            output.WriteLine(
                $"ActivityCount         : {activity.Count}");

            output.WriteLine(
                $"SnapshotAtUtc         : {activity.SnapshotAtUtc:O}");

            output.WriteLine("");

            output.WriteLine(
                "RECENT SHARED RUN ACTIVITY");

            output.WriteLine(
                "-----------------------------------------------------------");

            if (activity.Runs.Count == 0)
            {
                output.WriteLine(
                    "No shared run activity found.");
            }
            else
            {
                foreach (var run in activity.Runs)
                {
                    output.WriteLine(
                        $"SharedRunId           : {run.SharedRunId}");

                    output.WriteLine(
                        $"Status                : {run.Status}");

                    output.WriteLine(
                        $"PipelineKey           : {run.PipelineKey}");

                    output.WriteLine(
                        $"TenantId              : {run.TenantId}");

                    output.WriteLine(
                        $"AssignedInstanceId    : {run.AssignedRuntimeInstanceId}");

                    output.WriteLine(
                        $"LocalRunId            : {run.LocalRunId}");

                    output.WriteLine(
                        $"ExecutionId           : {run.ExecutionId}");

                    output.WriteLine(
                        $"CorrelationId         : {run.CorrelationId}");

                    output.WriteLine(
                        $"SubmittedAtUtc        : {run.SubmittedAtUtc:O}");

                    output.WriteLine(
                        $"UpdatedAtUtc          : {run.UpdatedAtUtc:O}");

                    output.WriteLine(
                        "-----------------------------------------------------------");
                }
            }

            output.WriteLine(
                "===========================================================");

            output.WriteLine("");
        }
    }
}