using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Results
{
    /// <summary>
    /// Writes readable pre-production recovery forensics output for integration test diagnostics.
    /// </summary>
    public static class ProductionRecoveryForensicsOutputWriter
    {
        /// <summary>
        /// Writes a compact recovery forensics summary.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="model">The recovery forensics read model.</param>
        public static void WriteSummary(
            ITestOutputHelper output,
            AiRuntimeRecoveryForensicsReadModel model)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(model);

            var record =
                model.Record;

            var replacementRuntimeInstanceId =
                ResolveReplacementRuntimeInstanceId(model, model.Timeline);

            var replacementLocalRunId =
                ResolveReplacementLocalRunId(model, model.Timeline);

            output.WriteLine("[RECOVERY FORENSICS SUMMARY]");
            output.WriteLine($"ForensicsId='{model.ForensicsId}'");
            output.WriteLine($"ExecutionId='{model.ExecutionId}'");
            output.WriteLine($"SharedRunId='{model.SharedRunId}'");
            output.WriteLine($"TenantId='{model.TenantId}'");
            output.WriteLine($"TenantGroupId='{record.Identity.TenantGroupId}'");
            output.WriteLine($"ControlPlaneId='{model.ControlPlaneId}'");
            output.WriteLine($"PipelineName='{record.Identity.PipelineName}'");
            output.WriteLine($"CreatedAtUtc='{model.CreatedAtUtc:O}'");
            output.WriteLine($"UpdatedAtUtc='{model.UpdatedAtUtc:O}'");
            output.WriteLine($"FailedRuntimeInstanceId='{record.Failure?.FailedRuntimeInstanceId}'");
            output.WriteLine($"FailedLocalRunId='{record.Failure?.FailedLocalRunId}'");
            output.WriteLine($"FailureSignal='{record.Failure?.FailureSignal}'");
            output.WriteLine($"ReplacementRuntimeInstanceId='{replacementRuntimeInstanceId}'");
            output.WriteLine($"ReplacementLocalRunId='{replacementLocalRunId}'");
            output.WriteLine($"TimelineCount='{model.Timeline.Count}'");
            output.WriteLine($"Timeline='{FormatTimeline(model.Timeline)}'");
        }

        /// <summary>
        /// Writes a compact recovery forensics search result list.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="result">The recovery forensics query result.</param>
        public static void WriteSearchResult(
            ITestOutputHelper output,
            AiRuntimeRecoveryForensicsQueryResult result)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(result);

            output.WriteLine("[RECOVERY FORENSICS LIST]");
            output.WriteLine($"Count='{result.Items.Count}'");

            var index =
                1;

            foreach (var item in result.Items)
            {
                var replacementRuntimeInstanceId =
                    ResolveReplacementRuntimeInstanceId(item, item.Timeline);

                var replacementLocalRunId =
                    ResolveReplacementLocalRunId(item, item.Timeline);

                output.WriteLine(
                    $"{index:00}. " +
                    $"ForensicsId='{item.ForensicsId}', " +
                    $"ExecutionId='{item.ExecutionId}', " +
                    $"SharedRunId='{item.SharedRunId}', " +
                    $"TenantId='{item.TenantId}', " +
                    $"TenantGroupId='{item.Record.Identity.TenantGroupId}', " +
                    $"ControlPlaneId='{item.ControlPlaneId}', " +
                    $"PipelineName='{item.Record.Identity.PipelineName}', " +
                    $"FailedRuntimeInstanceId='{item.Record.Failure?.FailedRuntimeInstanceId}', " +
                    $"FailedLocalRunId='{item.Record.Failure?.FailedLocalRunId}', " +
                    $"ReplacementRuntimeInstanceId='{replacementRuntimeInstanceId}', " +
                    $"ReplacementLocalRunId='{replacementLocalRunId}', " +
                    $"TimelineCount='{item.Timeline.Count}'.");

                index++;
            }
        }

        /// <summary>
        /// Writes a readable recovery forensics timeline.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="timeline">The recovery forensics timeline items.</param>
        public static void WriteTimeline(
            ITestOutputHelper output,
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(timeline);

            output.WriteLine("[RECOVERY FORENSICS TIMELINE]");
            output.WriteLine($"Count='{timeline.Count}'");

            for (var index = 0; index < timeline.Count; index++)
            {
                var item =
                    timeline[index];

                output.WriteLine(
                    $"{index + 1:00}. " +
                    $"TimestampUtc='{item.TimestampUtc:O}', " +
                    $"EventType='{item.EventType}', " +
                    $"Outcome='{item.Outcome}', " +
                    $"ExecutionId='{item.ExecutionId}', " +
                    $"SharedRunId='{item.SharedRunId}', " +
                    $"RuntimeInstanceId='{item.RuntimeInstanceId}', " +
                    $"LocalRunId='{item.LocalRunId}', " +
                    $"Reason='{item.Reason}'.");
            }
        }

        /// <summary>
        /// Writes a compact one-line recovery proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="model">The recovery forensics read model.</param>
        /// <param name="timeline">The resolved recovery forensics timeline.</param>
        /// <param name="tenantGroupId">The expected tenant group identifier.</param>
        /// <param name="diagnosticControlPlaneId">The diagnostic control-plane identifier expected by the test.</param>
        /// <param name="pipelineName">The expected pipeline name.</param>
        public static void WriteProof(
            ITestOutputHelper output,
            AiRuntimeRecoveryForensicsReadModel model,
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline,
            string tenantGroupId,
            string diagnosticControlPlaneId,
            string pipelineName)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantGroupId);
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticControlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var record =
                model.Record;

            var replacementRuntimeInstanceId =
                ResolveReplacementRuntimeInstanceId(model, timeline);

            var replacementLocalRunId =
                ResolveReplacementLocalRunId(model, timeline);

            output.WriteLine(
                "[RECOVERY FORENSICS PROOF] " +
                $"ForensicsId='{model.ForensicsId}', " +
                $"ExecutionId='{model.ExecutionId}', " +
                $"SharedRunId='{model.SharedRunId}', " +
                $"TenantId='{model.TenantId}', " +
                $"TenantGroupId='{tenantGroupId}', " +
                $"DiagnosticControlPlaneId='{diagnosticControlPlaneId}', " +
                $"ActualControlPlaneId='{model.ControlPlaneId}', " +
                $"PipelineName='{pipelineName}', " +
                $"FailedRuntimeInstanceId='{record.Failure?.FailedRuntimeInstanceId}', " +
                $"ReplacementRuntimeInstanceId='{replacementRuntimeInstanceId}', " +
                $"FailedLocalRunId='{record.Failure?.FailedLocalRunId}', " +
                $"ReplacementLocalRunId='{replacementLocalRunId}', " +
                $"Timeline='{FormatTimeline(timeline)}'.");
        }

        /// <summary>
        /// Resolves the replacement runtime instance id from the record first, then from the timeline.
        /// </summary>
        /// <param name="model">The recovery forensics read model.</param>
        /// <param name="timeline">The recovery timeline.</param>
        /// <returns>The replacement runtime instance id if available.</returns>
        private static string? ResolveReplacementRuntimeInstanceId(
            AiRuntimeRecoveryForensicsReadModel model,
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline)
        {
            return NullIfWhiteSpace(model.Record.Replacement?.ReplacementRuntimeInstanceId)
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ReplacementRuntimeSelected,
                        StringComparison.Ordinal))?.RuntimeInstanceId
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered,
                        StringComparison.Ordinal))?.RuntimeInstanceId
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCompleted,
                        StringComparison.Ordinal))?.RuntimeInstanceId;
        }

        /// <summary>
        /// Resolves the replacement local run id from the record first, then from the timeline.
        /// </summary>
        /// <param name="model">The recovery forensics read model.</param>
        /// <param name="timeline">The recovery timeline.</param>
        /// <returns>The replacement local run id if available.</returns>
        private static string? ResolveReplacementLocalRunId(
            AiRuntimeRecoveryForensicsReadModel model,
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline)
        {
            return NullIfWhiteSpace(model.Record.Replacement?.ReplacementLocalRunId)
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered,
                        StringComparison.Ordinal))?.LocalRunId
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ResumeContextSeeded,
                        StringComparison.Ordinal))?.LocalRunId
                ?? timeline.FirstOrDefault(item =>
                    string.Equals(
                        item.EventType,
                        AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCompleted,
                        StringComparison.Ordinal))?.LocalRunId;
        }

        /// <summary>
        /// Formats the recovery timeline as a compact event chain.
        /// </summary>
        /// <param name="timeline">The recovery forensics timeline items.</param>
        /// <returns>The formatted timeline.</returns>
        private static string FormatTimeline(
            IReadOnlyList<AiRuntimeRecoveryForensicsTimelineItem> timeline)
        {
            return string.Join(
                " -> ",
                timeline.Select(item => item.EventType));
        }

        /// <summary>
        /// Normalizes empty strings to null for fallback resolution.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The value when not empty; otherwise null.</returns>
        private static string? NullIfWhiteSpace(
            string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }
    }
}