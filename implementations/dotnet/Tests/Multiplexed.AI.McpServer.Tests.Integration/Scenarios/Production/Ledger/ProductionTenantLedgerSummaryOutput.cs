using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Writes dynamic tenant-scoped ledger summaries for production scenario diagnostics.
    /// </summary>
    public static class ProductionTenantLedgerSummaryOutput
    {
        private const int DefaultMaxLedgerEntriesPerTenant = 80;
        private const int DefaultMaxEventTypeRowsPerTenant = 30;
        private const int DefaultMaxLedgerEntriesPerExecution = 25;

        /// <summary>
        /// Writes a tenant-scoped ledger summary for any number of tenants.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="title">The summary title.</param>
        /// <param name="tenants">The tenant ledger summaries.</param>
        public static void Write(
            ITestOutputHelper output,
            string title,
            IReadOnlyCollection<ProductionTenantLedgerSummary> tenants)
        {
            Write(
                output,
                title,
                tenants,
                DefaultMaxLedgerEntriesPerTenant,
                DefaultMaxEventTypeRowsPerTenant,
                DefaultMaxLedgerEntriesPerExecution);
        }

        /// <summary>
        /// Writes a tenant-scoped ledger summary for any number of tenants.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="title">The summary title.</param>
        /// <param name="tenants">The tenant ledger summaries.</param>
        /// <param name="maxLedgerEntriesPerTenant">The maximum tenant-level ledger entries to print per tenant.</param>
        /// <param name="maxEventTypeRowsPerTenant">The maximum event type rows to print per tenant.</param>
        /// <param name="maxLedgerEntriesPerExecution">The maximum execution-level ledger entries to print per execution.</param>
        public static void Write(
            ITestOutputHelper output,
            string title,
            IReadOnlyCollection<ProductionTenantLedgerSummary> tenants,
            int maxLedgerEntriesPerTenant,
            int maxEventTypeRowsPerTenant,
            int maxLedgerEntriesPerExecution)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(tenants);

            output.WriteLine(string.Empty);
            output.WriteLine($"# {title}");
            output.WriteLine($"TenantCount='{tenants.Count}', MaxLedgerEntriesPerTenant='{maxLedgerEntriesPerTenant}', MaxEventTypeRowsPerTenant='{maxEventTypeRowsPerTenant}', MaxLedgerEntriesPerExecution='{maxLedgerEntriesPerExecution}'.");

            foreach (var tenant in tenants.OrderBy(tenant => tenant.TenantId, StringComparer.Ordinal))
            {
                WriteTenantSummary(
                    output,
                    tenant,
                    maxLedgerEntriesPerTenant,
                    maxEventTypeRowsPerTenant,
                    maxLedgerEntriesPerExecution);
            }
        }

        private static void WriteTenantSummary(
            ITestOutputHelper output,
            ProductionTenantLedgerSummary tenant,
            int maxLedgerEntriesPerTenant,
            int maxEventTypeRowsPerTenant,
            int maxLedgerEntriesPerExecution)
        {
            var entries =
                tenant.LedgerEntries
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.Sequence)
                    .ToArray();

            var infraEntries =
                entries.Count(IsInfraLedgerEntry);

            var controlPlaneEntries =
                entries.Count(entry => EventTypeStartsWith(entry, "control."));

            var recoveryEntries =
                entries.Count(entry =>
                    EventTypeContains(entry, "recovery") ||
                    MetadataContainsKeyPrefix(entry, "recovery.") ||
                    MetadataContainsKeyPrefix(entry, "property.recovery.") ||
                    MetadataContainsKeyPrefix(entry, "scaleout.recovery.") ||
                    MetadataContainsKeyPrefix(entry, "property.scaleout.recovery."));

            var runtimeInstanceEntries =
                entries.Count(entry => EventTypeContains(entry, "runtime-instance"));

            var scaleOutEntries =
                entries.Count(entry => EventTypeContains(entry, "runtime-scale-out"));

            var replayEntries =
                entries.Count(entry => EventTypeContains(entry, "replay"));

            var executionIds =
                tenant.ExecutionIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var runtimeInstanceIds =
                tenant.RuntimeInstanceIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            output.WriteLine(string.Empty);
            output.WriteLine($"## TENANT LEDGER SUMMARY - TenantId='{tenant.TenantId}'");
            output.WriteLine($"LedgerEntries='{entries.Length}', InfraEntries='{infraEntries}', ControlPlaneEntries='{controlPlaneEntries}', RuntimeInstanceEntries='{runtimeInstanceEntries}', RecoveryEntries='{recoveryEntries}', ScaleOutEntries='{scaleOutEntries}', ReplayEntries='{replayEntries}'.");
            output.WriteLine($"RuntimeInstanceCount='{runtimeInstanceIds.Length}', RuntimeInstanceIds='{string.Join(",", runtimeInstanceIds)}'.");
            output.WriteLine($"ExecutionCount='{executionIds.Length}', ExecutionIds='{string.Join(",", executionIds)}'.");
            output.WriteLine($"FirstTimestampUtc='{FormatTimestamp(entries.FirstOrDefault())}', LastTimestampUtc='{FormatTimestamp(entries.LastOrDefault())}'.");

            WriteExecutionLedgerBreakdown(
                output,
                tenant,
                entries,
                executionIds,
                maxLedgerEntriesPerExecution);

            WriteRecoveryFocusedLedgerEntries(
                output,
                tenant,
                entries,
                maxLedgerEntriesPerExecution);

            output.WriteLine(string.Empty);
            output.WriteLine($"### TENANT EVENT TYPE COUNTS - TenantId='{tenant.TenantId}'");

            foreach (var eventTypeGroup in entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.EventType))
                .GroupBy(entry => entry.EventType, StringComparer.Ordinal)
                .Select(group => new
                {
                    EventType = group.Key,
                    Count = group.Count(),
                    DistinctExecutions = group.Select(entry => entry.CorrelationContext.ExecutionId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count(),
                    DistinctRuns = group.Select(entry => entry.CorrelationContext.RunId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count(),
                    DistinctRuntimeInstances = group.Select(entry => entry.CorrelationContext.RuntimeInstanceId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count()
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.EventType, StringComparer.Ordinal)
                .Take(maxEventTypeRowsPerTenant))
            {
                output.WriteLine($"EventType='{eventTypeGroup.EventType}', Count='{eventTypeGroup.Count}', DistinctExecutions='{eventTypeGroup.DistinctExecutions}', DistinctRuns='{eventTypeGroup.DistinctRuns}', DistinctRuntimeInstances='{eventTypeGroup.DistinctRuntimeInstances}'.");
            }

            output.WriteLine(string.Empty);
            output.WriteLine($"### FIRST TENANT LEDGER ENTRIES - TenantId='{tenant.TenantId}', Max='{maxLedgerEntriesPerTenant}'");

            var index =
                1;

            foreach (var entry in entries.Take(maxLedgerEntriesPerTenant))
            {
                output.WriteLine(
                    $"{index:00}. Timestamp='{entry.TimestampUtc:O}', Sequence='{entry.Sequence}', EventType='{entry.EventType}', Outcome='{entry.Outcome}', Operation='{entry.CorrelationContext.Operation}', RuntimeInstanceId='{entry.CorrelationContext.RuntimeInstanceId}', ExecutionId='{entry.CorrelationContext.ExecutionId}', RunId='{entry.CorrelationContext.RunId}', PipelineName='{FirstNonEmpty(entry.CorrelationContext.PipelineName, GetMetadataValue(entry, "pipelineName", "property.pipelineName", "pipeline.name", "property.pipeline.name"))}', Reason='{FirstNonEmpty(entry.Reason, GetMetadataValue(entry, "reason", "property.reason", "failure.reason", "property.failure.reason"))}', TenantId='{FirstNonEmpty(GetMetadataValue(entry, "tenantId", "property.tenantId", "tenant.id", "property.tenant.id", "scaleout.tenant.id", "property.scaleout.tenant.id"), tenant.TenantId)}'.");

                index++;
            }
        }

        private static void WriteExecutionLedgerBreakdown(
            ITestOutputHelper output,
            ProductionTenantLedgerSummary tenant,
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            IReadOnlyCollection<string> executionIds,
            int maxLedgerEntriesPerExecution)
        {
            output.WriteLine(string.Empty);
            output.WriteLine($"### TENANT EXECUTION LEDGER BREAKDOWN - TenantId='{tenant.TenantId}', MaxPerExecution='{maxLedgerEntriesPerExecution}'");

            foreach (var executionId in executionIds.OrderBy(value => value, StringComparer.Ordinal))
            {
                var executionEntries =
                    entries
                        .Where(entry => EntryBelongsToExecution(entry, executionId))
                        .OrderBy(entry => entry.TimestampUtc)
                        .ThenBy(entry => entry.Sequence)
                        .ToArray();

                var runtimeInstanceIds =
                    executionEntries
                        .Select(entry => entry.CorrelationContext.RuntimeInstanceId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                var runIds =
                    executionEntries
                        .Select(entry => entry.CorrelationContext.RunId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                var stepStartedCount =
                    executionEntries.Count(entry => string.Equals(entry.EventType, "step.started", StringComparison.Ordinal));

                var stepCompletedCount =
                    executionEntries.Count(entry => string.Equals(entry.EventType, "step.completed", StringComparison.Ordinal));

                var replayEntries =
                    executionEntries.Count(entry => EventTypeContains(entry, "replay"));

                var completionEvidence =
                    executionEntries.Any(entry =>
                        string.Equals(entry.EventType, "run.completed", StringComparison.Ordinal) ||
                        string.Equals(entry.EventType, "execution.completed", StringComparison.Ordinal) ||
                        string.Equals(entry.EventType, "dag.completed", StringComparison.Ordinal) ||
                        (EventTypeContains(entry, "completed") &&
                        string.Equals(entry.Outcome.ToString(), "Completed", StringComparison.OrdinalIgnoreCase)));

                output.WriteLine(string.Empty);
                output.WriteLine($"#### EXECUTION LEDGER SUMMARY - TenantId='{tenant.TenantId}', ExecutionId='{executionId}'");
                output.WriteLine($"LedgerEntries='{executionEntries.Length}', RuntimeInstanceCount='{runtimeInstanceIds.Length}', RuntimeInstanceIds='{string.Join(",", runtimeInstanceIds)}', RunIds='{string.Join(",", runIds)}', StepStarted='{stepStartedCount}', StepCompleted='{stepCompletedCount}', ReplayEntries='{replayEntries}', CompletionEvidence='{completionEvidence}', FirstTimestampUtc='{FormatTimestamp(executionEntries.FirstOrDefault())}', LastTimestampUtc='{FormatTimestamp(executionEntries.LastOrDefault())}'.");

                var index =
                    1;

                foreach (var entry in executionEntries.Take(maxLedgerEntriesPerExecution))
                {
                    output.WriteLine(
                        $"{index:00}. Timestamp='{entry.TimestampUtc:O}', Sequence='{entry.Sequence}', EventType='{entry.EventType}', Outcome='{entry.Outcome}', Operation='{entry.CorrelationContext.Operation}', RuntimeInstanceId='{entry.CorrelationContext.RuntimeInstanceId}', ExecutionId='{entry.CorrelationContext.ExecutionId}', RunId='{entry.CorrelationContext.RunId}', PipelineName='{FirstNonEmpty(entry.CorrelationContext.PipelineName, GetMetadataValue(entry, "pipelineName", "property.pipelineName", "pipeline.name", "property.pipeline.name"))}', Reason='{FirstNonEmpty(entry.Reason, GetMetadataValue(entry, "reason", "property.reason", "failure.reason", "property.failure.reason"))}'.");

                    index++;
                }
            }
        }

        private static void WriteRecoveryFocusedLedgerEntries(
            ITestOutputHelper output,
            ProductionTenantLedgerSummary tenant,
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            int maxLedgerEntries)
        {
            var recoveryEntries =
                entries
                    .Where(IsRecoveryFocusedEntry)
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.Sequence)
                    .ToArray();

            output.WriteLine(string.Empty);
            output.WriteLine($"### TENANT RECOVERY LEDGER ENTRIES - TenantId='{tenant.TenantId}', Total='{recoveryEntries.Length}', Max='{maxLedgerEntries}'");

            var index =
                1;

            foreach (var entry in recoveryEntries.Take(maxLedgerEntries))
            {
                output.WriteLine(
                    $"{index:00}. Timestamp='{entry.TimestampUtc:O}', Sequence='{entry.Sequence}', EventType='{entry.EventType}', Outcome='{entry.Outcome}', Operation='{entry.CorrelationContext.Operation}', RuntimeInstanceId='{entry.CorrelationContext.RuntimeInstanceId}', ExecutionId='{entry.CorrelationContext.ExecutionId}', RunId='{entry.CorrelationContext.RunId}', PipelineName='{FirstNonEmpty(entry.CorrelationContext.PipelineName, GetMetadataValue(entry, "pipelineName", "property.pipelineName", "pipeline.name", "property.pipeline.name"))}', Reason='{FirstNonEmpty(entry.Reason, GetMetadataValue(entry, "reason", "property.reason", "failure.reason", "property.failure.reason"))}', ForensicsId='{GetMetadataValue(entry, "recovery.forensicsId", "property.recovery.forensicsId", "scaleout.recovery.forensicsId", "property.scaleout.recovery.forensicsId")}', RecoveryMode='{GetMetadataValue(entry, "recovery.mode", "property.recovery.mode", "scaleout.recovery.mode", "property.scaleout.recovery.mode")}'.");

                index++;
            }
        }

        private static bool EntryBelongsToExecution(
            AiDecisionLedgerEntry entry,
            string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                return false;
            }

            return string.Equals(entry.CorrelationContext.ExecutionId, executionId, StringComparison.Ordinal) ||
                MetadataValueEquals(
                    entry,
                    executionId,
                    "executionId",
                    "property.executionId",
                    "execution.id",
                    "property.execution.id",
                    "recovery.failedExecutionId",
                    "property.recovery.failedExecutionId",
                    "scaleout.recovery.failedExecutionId",
                    "property.scaleout.recovery.failedExecutionId");
        }

        private static bool IsRecoveryFocusedEntry(
            AiDecisionLedgerEntry entry)
        {
            return EventTypeContains(entry, "recovery") ||
                EventTypeContains(entry, "runtime-scale-out") ||
                EventTypeContains(entry, "remote-shared-run-dispatch") ||
                EventTypeContains(entry, "runtime-host-creation") ||
                EventTypeContains(entry, "runtime-process-host-creation") ||
                EventTypeContains(entry, "runtime-instance-mark-unhealthy") ||
                EventTypeContains(entry, "runtime-instance-mark-unsafe") ||
                EventTypeContains(entry, "runtime-instance-suppress-capacity") ||
                MetadataContainsKeyPrefix(entry, "recovery.") ||
                MetadataContainsKeyPrefix(entry, "property.recovery.") ||
                MetadataContainsKeyPrefix(entry, "scaleout.recovery.") ||
                MetadataContainsKeyPrefix(entry, "property.scaleout.recovery.") ||
                MetadataHasValue(
                    entry,
                    "failed.runtimeInstanceId",
                    "property.failed.runtimeInstanceId",
                    "failed.localRunId",
                    "property.failed.localRunId");
        }

        private static bool IsInfraLedgerEntry(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return EventTypeStartsWith(entry, "control.") ||
                EventTypeContains(entry, "runtime-instance") ||
                EventTypeContains(entry, "runtime-execution-recovery") ||
                EventTypeContains(entry, "recovery") ||
                string.Equals(entry.CorrelationContext.Operation, "control-plane", StringComparison.OrdinalIgnoreCase);
        }

        private static bool EventTypeStartsWith(
            AiDecisionLedgerEntry entry,
            string prefix)
        {
            return !string.IsNullOrWhiteSpace(entry.EventType) &&
                entry.EventType.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool EventTypeContains(
            AiDecisionLedgerEntry entry,
            string token)
        {
            return !string.IsNullOrWhiteSpace(entry.EventType) &&
                entry.EventType.Contains(token, StringComparison.Ordinal);
        }

        private static bool MetadataContainsKeyPrefix(
            AiDecisionLedgerEntry entry,
            string prefix)
        {
            if (entry.Metadata is null)
            {
                return false;
            }

            return entry.Metadata.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool MetadataHasValue(
            AiDecisionLedgerEntry entry,
            params string[] keys)
        {
            return !string.IsNullOrWhiteSpace(
                GetMetadataValue(
                    entry,
                    keys));
        }

        private static bool MetadataValueEquals(
            AiDecisionLedgerEntry entry,
            string expectedValue,
            params string[] keys)
        {
            if (entry.Metadata is null)
            {
                return false;
            }

            foreach (var key in keys)
            {
                if (entry.Metadata.TryGetValue(key, out var value) &&
                    string.Equals(value, expectedValue, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetMetadataValue(
            AiDecisionLedgerEntry entry,
            params string[] keys)
        {
            if (entry.Metadata is null)
            {
                return string.Empty;
            }

            foreach (var key in keys)
            {
                if (entry.Metadata.TryGetValue(key, out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(
            params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string FormatTimestamp(
            AiDecisionLedgerEntry? entry)
        {
            return entry is null
                ? string.Empty
                : entry.TimestampUtc.ToString("O");
        }
    }
}