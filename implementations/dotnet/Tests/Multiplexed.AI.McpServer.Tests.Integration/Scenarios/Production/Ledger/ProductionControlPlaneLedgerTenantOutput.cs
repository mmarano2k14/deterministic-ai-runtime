using System.Globalization;
using System.Text;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Writes tenant-scoped MCP ledger output for production scenario proofs.
    /// </summary>
    public static class ProductionControlPlaneLedgerTenantOutput
    {
        /// <summary>
        /// Writes a tenant-scoped ledger dump with event summaries.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="records">The ledger records returned through MCP.</param>
        /// <param name="tenantIds">The tenant identifiers to display.</param>
        /// <param name="maxEventsPerTenant">The maximum number of detailed events to display per tenant.</param>
        public static void WriteTenantLedgerSummary(
            ITestOutputHelper output,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            IReadOnlyCollection<string> tenantIds,
            int maxEventsPerTenant = 250)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(tenantIds);

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("# MCP LEDGER BY TENANT");
            builder.AppendLine("Source: MCP tool 'observability.ledger.query'.");
            builder.AppendLine($"TotalRecords: {records.Count.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"ControlPlaneRecords: {records.Count(record => record.EventType.StartsWith("control.", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine();
            builder.AppendLine("Legend:");
            builder.AppendLine("  tenant=infra means infrastructure/control-plane records without a direct tenant context.");
            builder.AppendLine("  Tenant sections include both tenant-scoped business events and runtime-scoped infrastructure events for that tenant runtime prefix when available.");

            foreach (var tenantId in tenantIds.Where(tenantId => !string.IsNullOrWhiteSpace(tenantId)).Distinct(StringComparer.Ordinal).OrderBy(tenantId => tenantId, StringComparer.Ordinal))
            {
                AppendTenantSection(builder, records, tenantId, maxEventsPerTenant);
            }

            AppendInfrastructureSection(builder, records);

            output.WriteLine(builder.ToString());
        }

        private static void AppendTenantSection(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string tenantId,
            int maxEventsPerTenant)
        {
            var tenantRecords = records
                .Where(record => BelongsToTenant(record, tenantId))
                .OrderBy(record => record.CapturedAtUtc)
                .ToArray();

            builder.AppendLine();
            builder.AppendLine($"## Tenant: {tenantId}");
            builder.AppendLine($"Records: {tenantRecords.Length.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"ControlPlaneRecords: {tenantRecords.Count(record => record.EventType.StartsWith("control.", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"RuntimeRecords: {tenantRecords.Count(record => !record.EventType.StartsWith("control.", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine();

            AppendOutcomeSummary(builder, tenantRecords);
            AppendCategorySummary(builder, tenantRecords);
            AppendEventTypeSummary(builder, tenantRecords, maxEventTypes: 40);
            AppendTenantBusinessPathSummary(builder, tenantRecords);
            AppendDetailedEvents(builder, tenantRecords, maxEventsPerTenant);
        }

        private static void AppendInfrastructureSection(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            var infrastructureRecords = records
                .Where(IsInfrastructureRecord)
                .OrderBy(record => record.CapturedAtUtc)
                .ToArray();

            builder.AppendLine();
            builder.AppendLine("## Infrastructure / control-plane records without direct tenant context");
            builder.AppendLine($"Records: {infrastructureRecords.Length.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine();
            AppendOutcomeSummary(builder, infrastructureRecords);
            AppendCategorySummary(builder, infrastructureRecords);
            AppendEventTypeSummary(builder, infrastructureRecords, maxEventTypes: 40);
        }

        private static void AppendOutcomeSummary(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            builder.AppendLine("Outcome summary:");

            foreach (var group in records
                .GroupBy(record => record.Outcome)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal))
            {
                builder.AppendLine($"  - {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
            }

            if (records.Count == 0)
            {
                builder.AppendLine("  - none: 0");
            }

            builder.AppendLine();
        }

        private static void AppendCategorySummary(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            builder.AppendLine("Category summary:");

            foreach (var group in records
                .GroupBy(record => record.Category)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal))
            {
                builder.AppendLine($"  - {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
            }

            if (records.Count == 0)
            {
                builder.AppendLine("  - none: 0");
            }

            builder.AppendLine();
        }

        private static void AppendEventTypeSummary(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            int maxEventTypes)
        {
            builder.AppendLine("Event type summary:");

            foreach (var group in records
                .GroupBy(record => record.EventType, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Take(maxEventTypes))
            {
                builder.AppendLine($"  - {group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}");
            }

            var hiddenCount = records
                .GroupBy(record => record.EventType, StringComparer.Ordinal)
                .Count() - maxEventTypes;

            if (hiddenCount > 0)
            {
                builder.AppendLine($"  - ... {hiddenCount.ToString(CultureInfo.InvariantCulture)} more event types omitted");
            }

            if (records.Count == 0)
            {
                builder.AppendLine("  - none: 0");
            }

            builder.AppendLine();
        }

        private static void AppendTenantBusinessPathSummary(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            builder.AppendLine("Business path summary:");
            AppendBusinessPathLine(builder, records, "admission", "runtime-admission-decision");
            AppendBusinessPathLine(builder, records, "scaleOut", "runtime-scale-out-request-publish");
            AppendBusinessPathLine(builder, records, "watcher", "runtime-scale-out-request-watch");
            AppendBusinessPathLine(builder, records, "providerSelection", "runtime-scale-out-provider-selection");
            AppendBusinessPathLine(builder, records, "hostCreation", "runtime-host-creation");
            AppendBusinessPathLine(builder, records, "processHostCreation", "runtime-process-host-creation");
            AppendBusinessPathLine(builder, records, "runtimeVisibility", "runtime-instance-get", "runtime-instance-capacity-get", "runtime-instance-list");
            AppendBusinessPathLine(builder, records, "healthIsolation", "runtime-instance-mark-unhealthy");
            AppendBusinessPathLine(builder, records, "recoveryReconcile", "runtime-execution-recovery-reconcile");
            AppendBusinessPathLine(builder, records, "redispatch", "remote-shared-run-dispatch");
            builder.AppendLine();
        }

        private static void AppendBusinessPathLine(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string label,
            params string[] operations)
        {
            var count = records.Count(record => operations.Any(operation => record.EventType.Contains(operation, StringComparison.Ordinal)));
            var succeeded = records.Any(record => operations.Any(operation => record.EventType.Contains(operation, StringComparison.Ordinal)) && record.Outcome == AiDecisionLedgerOutcome.Succeeded);
            var completedWithIssues = records.Any(record => operations.Any(operation => record.EventType.Contains(operation, StringComparison.Ordinal)) && record.Outcome == AiDecisionLedgerOutcome.CompletedWithIssues);
            var failed = records.Any(record => operations.Any(operation => record.EventType.Contains(operation, StringComparison.Ordinal)) && record.Outcome == AiDecisionLedgerOutcome.Failed);
            var marker = succeeded ? "PASS" : completedWithIssues ? "WARN" : failed ? "FAIL" : "MISS";
            builder.AppendLine($"  - [{marker}] {label}: records={count.ToString(CultureInfo.InvariantCulture)}");
        }

        private static void AppendDetailedEvents(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            int maxEventsPerTenant)
        {
            builder.AppendLine($"Detailed events first {maxEventsPerTenant.ToString(CultureInfo.InvariantCulture)}:");

            foreach (var record in records.Take(maxEventsPerTenant))
            {
                builder.Append("  - ");
                builder.Append(record.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                builder.Append(" | ");
                builder.Append(record.Category);
                builder.Append(" | ");
                builder.Append(record.Outcome);
                builder.Append(" | ");
                builder.Append(record.EventType);
                builder.Append(" | runtime=");
                builder.Append(TryGet(record, "runtime.instance.id") ?? record.Context.RuntimeInstanceId ?? record.Context.ExecutionId ?? "-");
                builder.Append(" | run=");
                builder.Append(TryGet(record, "run.id") ?? record.Context.RunId ?? "-");
                builder.Append(" | execution=");
                builder.Append(record.Context.ExecutionId ?? "-");
                builder.Append(" | pipeline=");
                builder.Append(TryGet(record, "pipeline.name") ?? record.Context.PipelineName ?? "-");
                builder.Append(" | reason=");
                builder.Append(record.Reason ?? TryGet(record, "failure.reason") ?? "-");
                builder.AppendLine();
            }

            if (records.Count > maxEventsPerTenant)
            {
                builder.AppendLine($"  - ... {(records.Count - maxEventsPerTenant).ToString(CultureInfo.InvariantCulture)} more records omitted for this tenant");
            }
        }

        private static bool BelongsToTenant(
            CapturedIntegrationLedgerRecord record,
            string tenantId)
        {
            return string.Equals(TryGet(record, "tenant.id"), tenantId, StringComparison.Ordinal) ||
                string.Equals(TryGet(record, "tenantId"), tenantId, StringComparison.Ordinal) ||
                ContainsTenant(record.Context.RuntimeInstanceId, tenantId) ||
                ContainsTenant(record.Context.ExecutionId, tenantId) ||
                ContainsTenant(record.Context.PipelineName, tenantId) ||
                ContainsTenant(TryGet(record, "runtime.instance.id"), tenantId) ||
                ContainsTenant(TryGet(record, "pipeline.key"), tenantId) ||
                ContainsTenant(TryGet(record, "run.id"), tenantId) ||
                ContainsTenant(TryGet(record, "execution.id"), tenantId);
        }

        private static bool IsInfrastructureRecord(
            CapturedIntegrationLedgerRecord record)
        {
            return string.IsNullOrWhiteSpace(TryGet(record, "tenant.id")) &&
                string.IsNullOrWhiteSpace(TryGet(record, "tenantId"));
        }

        private static bool ContainsTenant(
            string? value,
            string tenantId)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.Contains(tenantId, StringComparison.Ordinal);
        }

        private static string? TryGet(
            CapturedIntegrationLedgerRecord record,
            string key)
        {
            return record.Metadata.TryGetValue(key, out var value) ? value : null;
        }
    }
}
