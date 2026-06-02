using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
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
    }
}