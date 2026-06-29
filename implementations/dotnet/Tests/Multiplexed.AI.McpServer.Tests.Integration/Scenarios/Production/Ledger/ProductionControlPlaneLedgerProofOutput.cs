using System.Text;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Writes public, human-readable control-plane ledger proof output for production scenarios.
    /// </summary>
    public static class ProductionControlPlaneLedgerProofOutput
    {
        /// <summary>
        /// Writes a public concurrent runtime recovery ledger proof.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="context">The public proof context.</param>
        public static void WriteConcurrentRuntimeRecoveryProof(
            ITestOutputHelper output,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            ProductionControlPlaneLedgerProofContext context)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(records);
            ArgumentNullException.ThrowIfNull(context);

            var orderedRecords = records
                .OrderBy(record => record.CapturedAtUtc)
                .ToArray();

            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("# CONTROL PLANE LEDGER PROOF - HTTP PROCESS HOST CONCURRENT RECOVERY");
            builder.AppendLine($"ControlPlaneId: {context.ControlPlaneId}");
            builder.AppendLine($"TenantA: {context.TenantAId}");
            builder.AppendLine($"TenantB: {context.TenantBId}");
            builder.AppendLine($"FailedRuntimeA: {context.TenantAFailedRuntimeInstanceId}");
            builder.AppendLine($"FailedRuntimeB: {context.TenantBFailedRuntimeInstanceId}");
            builder.AppendLine($"ControlRuntime: {context.ControlRuntimeInstanceId}");
            builder.AppendLine($"RecoveredWork: {context.RecoveredWorkCount}/{context.ExpectedRecoveredWorkCount}");
            builder.AppendLine();
            builder.AppendLine("Executive proof:");
            builder.AppendLine("  This test proves that the control plane can explain a real HTTP process-host scale-out,");
            builder.AppendLine("  runtime visibility, stale runtime isolation, and concurrent recovery without tenant or incident leakage.");
            builder.AppendLine();
            builder.AppendLine("Proof phases:");
            AppendPhase(builder, "1. Scale-out request persisted", orderedRecords, "runtime-scale-out-request-publish");
            AppendPhase(builder, "2. Scale-out watcher observed request", orderedRecords, "runtime-scale-out-request-watch");
            AppendPhase(builder, "3. Provider selected", orderedRecords, "runtime-scale-out-provider-selection");
            AppendPhase(builder, "4. Runtime host manager created host", orderedRecords, "runtime-host-creation");
            AppendPhase(builder, "5. Process runtime host started", orderedRecords, "runtime-process-host-creation");
            AppendPhase(builder, "6. Runtime capacity became visible", orderedRecords, "runtime-instance-capacity-publish");
            AppendRuntimeVisibilityPhase(builder, orderedRecords);
            AppendPhase(builder, "8. Failed runtime marked unhealthy", orderedRecords, "runtime-instance-mark-unhealthy");
            AppendPhase(builder, "9. Execution recovery reconciled assigned work", orderedRecords, "runtime-execution-recovery-reconcile");
            AppendPhase(builder, "10. Recovered work redispatched", orderedRecords, "remote-shared-run-dispatch");
            builder.AppendLine();
            builder.AppendLine("Tenant business proof:");
            AppendTenantBusinessProof(builder, orderedRecords, context.TenantAId);
            AppendTenantBusinessProof(builder, orderedRecords, context.TenantBId);
            builder.AppendLine();
            builder.AppendLine("Tenant diagnostic event volume:");
            AppendTenantDiagnosticVolume(builder, orderedRecords, context.TenantAId);
            AppendTenantDiagnosticVolume(builder, orderedRecords, context.TenantBId);
            builder.AppendLine("  Note: event counts include asynchronous polling, admission retries, watcher cycles, health checks, and recovery timing.");
            builder.AppendLine("  The contractual proof is tenant path completeness plus zero cross-tenant recovery leakage, not equal event counts.");
            builder.AppendLine();
            builder.AppendLine("Safety invariants:");
            builder.AppendLine($"  CrossTenantLeakDetected: {context.CrossTenantLeakDetected}");
            builder.AppendLine($"  CrossIncidentLeakDetected: {context.CrossIncidentLeakDetected}");
            builder.AppendLine($"  DuplicateRecoveryDetected: {context.DuplicateRecoveryDetected}");
            builder.AppendLine($"  SelfRedispatchDetected: {context.SelfRedispatchDetected}");
            builder.AppendLine();
            builder.AppendLine("Critical timeline:");
            AppendCriticalTimeline(builder, orderedRecords);
            builder.AppendLine();
            builder.AppendLine("Notes:");
            builder.AppendLine("  tenant=infra means the ledger event is an infrastructure/control-plane operation without a direct tenant context.");
            builder.AppendLine("  In real process-host mode, child-process registration may not be captured by the parent in-memory test ledger.");
            builder.AppendLine("  Runtime visibility is therefore proven through registry and capacity lookups from the parent control plane.");
            output.WriteLine(builder.ToString());
        }

        /// <summary>
        /// Appends a runtime visibility proof phase.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="records">The captured ledger records.</param>
        private static void AppendRuntimeVisibilityPhase(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            var registerCount = CountOperation(records, "runtime-instance-register");
            var getCount = CountOperation(records, "runtime-instance-get");
            var capacityGetCount = CountOperation(records, "runtime-instance-capacity-get");
            var listCount = CountOperation(records, "runtime-instance-list");
            var total = registerCount + getCount + capacityGetCount + listCount;
            var succeeded =
                HasSucceededOperation(records, "runtime-instance-register") ||
                HasSucceededOperation(records, "runtime-instance-get") ||
                HasSucceededOperation(records, "runtime-instance-capacity-get") ||
                HasSucceededOperation(records, "runtime-instance-list");
            var completedWithIssues =
                HasCompletedWithIssuesOperation(records, "runtime-instance-register") ||
                HasCompletedWithIssuesOperation(records, "runtime-instance-get") ||
                HasCompletedWithIssuesOperation(records, "runtime-instance-capacity-get") ||
                HasCompletedWithIssuesOperation(records, "runtime-instance-list");
            var failed =
                HasFailedOperation(records, "runtime-instance-register") ||
                HasFailedOperation(records, "runtime-instance-get") ||
                HasFailedOperation(records, "runtime-instance-capacity-get") ||
                HasFailedOperation(records, "runtime-instance-list");
            var marker = ResolveMarker(succeeded, completedWithIssues, failed);
            builder.AppendLine($"[{marker}] 7. Runtime instance became visible through registry/capacity lookup (runtime-instance-register|get|capacity-get|list) records={total} register={registerCount} get={getCount} capacityGet={capacityGetCount} list={listCount}");
        }

        /// <summary>
        /// Appends a proof phase.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="label">The phase label.</param>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        private static void AppendPhase(
            StringBuilder builder,
            string label,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            var matchingRecords = records
                .Where(record => record.EventType.Contains(operation, StringComparison.Ordinal))
                .ToArray();
            var succeeded = matchingRecords.Any(record => record.Outcome == AiDecisionLedgerOutcome.Succeeded);
            var completedWithIssues = matchingRecords.Any(record => record.Outcome == AiDecisionLedgerOutcome.CompletedWithIssues);
            var failed = matchingRecords.Any(record => record.Outcome == AiDecisionLedgerOutcome.Failed);
            var marker = ResolveMarker(succeeded, completedWithIssues, failed);
            builder.AppendLine($"[{marker}] {label} ({operation}) records={matchingRecords.Length}");
        }

        /// <summary>
        /// Appends qualitative tenant business proof lines.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        private static void AppendTenantBusinessProof(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string tenantId)
        {
            var hasAdmission = HasTenantSucceededOrCompleted(records, tenantId, "runtime-admission-decision");
            var hasScaleOut = HasTenantSucceededOrCompleted(records, tenantId, "runtime-scale-out-request-publish") || HasTenantSucceededOrCompleted(records, tenantId, "runtime-scale-out-request-watch") || HasTenantSucceededOrCompleted(records, tenantId, "runtime-scale-out-provider-selection");
            var hasHostCreation = HasTenantSucceededOrCompleted(records, tenantId, "runtime-host-creation") || HasTenantSucceededOrCompleted(records, tenantId, "runtime-process-host-creation");
            var hasDispatch = HasTenantSucceededOrCompleted(records, tenantId, "remote-shared-run-dispatch");
            var marker = hasAdmission && hasScaleOut && hasHostCreation && hasDispatch ? "PASS" : "MISS";
            builder.AppendLine($"[{marker}] {tenantId}: business path complete admission={FormatBool(hasAdmission)} scaleOut={FormatBool(hasScaleOut)} hostCreation={FormatBool(hasHostCreation)} dispatch={FormatBool(hasDispatch)}");
        }

        /// <summary>
        /// Appends non-contractual tenant diagnostic event volume lines.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        private static void AppendTenantDiagnosticVolume(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string tenantId)
        {
            var tenantRecords = records
                .Where(record => string.Equals(TryGet(record, "tenant.id"), tenantId, StringComparison.Ordinal))
                .ToArray();
            var admissionCount = tenantRecords.Count(record => record.EventType.Contains("runtime-admission-decision", StringComparison.Ordinal));
            var scaleOutCount = tenantRecords.Count(record => record.EventType.Contains("runtime-scale-out", StringComparison.Ordinal));
            var hostCreationCount = tenantRecords.Count(record => record.EventType.Contains("runtime-host-creation", StringComparison.Ordinal) || record.EventType.Contains("runtime-process-host-creation", StringComparison.Ordinal));
            var dispatchCount = tenantRecords.Count(record => record.EventType.Contains("remote-shared-run-dispatch", StringComparison.Ordinal));
            builder.AppendLine($"  {tenantId}: records={tenantRecords.Length} admission={admissionCount} scaleOut={scaleOutCount} hostCreation={hostCreationCount} dispatch={dispatchCount}");
        }

        /// <summary>
        /// Appends a compact critical timeline with one representative record per public phase.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="records">The captured ledger records.</param>
        private static void AppendCriticalTimeline(
            StringBuilder builder,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            var selectedRecords = new List<CapturedIntegrationLedgerRecord>();
            AddFirst(selectedRecords, records, "runtime-admission-decision", AiDecisionLedgerOutcome.CompletedWithIssues);
            AddFirst(selectedRecords, records, "runtime-scale-out-request-publish", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-process-host-creation", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-host-creation", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-scale-out-provider-selection", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-scale-out-request-watch", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-instance-get", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-instance-capacity-get", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-instance-mark-unhealthy", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "runtime-execution-recovery-reconcile", AiDecisionLedgerOutcome.Succeeded);
            AddFirst(selectedRecords, records, "remote-shared-run-dispatch", AiDecisionLedgerOutcome.Succeeded);

            foreach (var record in selectedRecords
                .DistinctBy(record => record.EventType + "|" + record.CapturedAtUtc.ToString("O") + "|" + record.Context.ExecutionId)
                .OrderBy(record => record.CapturedAtUtc))
            {
                AppendRecord(builder, record);
            }
        }

        /// <summary>
        /// Adds the first matching ledger record.
        /// </summary>
        /// <param name="selectedRecords">The selected record list.</param>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <param name="outcome">The desired outcome.</param>
        private static void AddFirst(
            ICollection<CapturedIntegrationLedgerRecord> selectedRecords,
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation,
            AiDecisionLedgerOutcome outcome)
        {
            var record = records
                .Where(record => record.EventType.Contains(operation, StringComparison.Ordinal) && record.Outcome == outcome)
                .OrderBy(record => record.CapturedAtUtc)
                .FirstOrDefault();
            if (record is not null)
            {
                selectedRecords.Add(record);
            }
        }

        /// <summary>
        /// Appends a compact ledger record line.
        /// </summary>
        /// <param name="builder">The output builder.</param>
        /// <param name="record">The ledger record.</param>
        private static void AppendRecord(
            StringBuilder builder,
            CapturedIntegrationLedgerRecord record)
        {
            builder.Append("  - ");
            builder.Append(record.CapturedAtUtc.ToString("O"));
            builder.Append(" | ");
            builder.Append(record.Outcome);
            builder.Append(" | ");
            builder.Append(record.EventType);
            builder.Append(" | runtime=");
            builder.Append(TryGet(record, "runtime.instance.id") ?? record.Context.ExecutionId ?? "-");
            builder.Append(" | tenant=");
            builder.Append(ResolveTenantDisplay(record));
            builder.Append(" | reason=");
            builder.Append(record.Reason ?? record.Metadata.GetValueOrDefault("failure.reason") ?? "-");
            builder.AppendLine();
        }

        /// <summary>
        /// Counts ledger records for an operation.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns>The matching record count.</returns>
        private static int CountOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            return records.Count(record => record.EventType.Contains(operation, StringComparison.Ordinal));
        }

        /// <summary>
        /// Determines whether an operation has a succeeded ledger record.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns><c>true</c> when a succeeded record exists; otherwise, <c>false</c>.</returns>
        private static bool HasSucceededOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            return records.Any(record => record.EventType.Contains(operation, StringComparison.Ordinal) && record.Outcome == AiDecisionLedgerOutcome.Succeeded);
        }

        /// <summary>
        /// Determines whether an operation has a completed-with-issues ledger record.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns><c>true</c> when a completed-with-issues record exists; otherwise, <c>false</c>.</returns>
        private static bool HasCompletedWithIssuesOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            return records.Any(record => record.EventType.Contains(operation, StringComparison.Ordinal) && record.Outcome == AiDecisionLedgerOutcome.CompletedWithIssues);
        }

        /// <summary>
        /// Determines whether an operation has a failed ledger record.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns><c>true</c> when a failed record exists; otherwise, <c>false</c>.</returns>
        private static bool HasFailedOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            return records.Any(record => record.EventType.Contains(operation, StringComparison.Ordinal) && record.Outcome == AiDecisionLedgerOutcome.Failed);
        }

        /// <summary>
        /// Determines whether a tenant has a successful or completed-with-issues operation record.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns><c>true</c> when the tenant has the operation record; otherwise, <c>false</c>.</returns>
        private static bool HasTenantSucceededOrCompleted(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string tenantId,
            string operation)
        {
            return records.Any(record =>
                string.Equals(TryGet(record, "tenant.id"), tenantId, StringComparison.Ordinal) &&
                record.EventType.Contains(operation, StringComparison.Ordinal) &&
                record.Outcome is AiDecisionLedgerOutcome.Succeeded or AiDecisionLedgerOutcome.CompletedWithIssues);
        }

        /// <summary>
        /// Resolves the output marker for a phase.
        /// </summary>
        /// <param name="succeeded">A value indicating whether a succeeded record exists.</param>
        /// <param name="completedWithIssues">A value indicating whether a completed-with-issues record exists.</param>
        /// <param name="failed">A value indicating whether a failed record exists.</param>
        /// <returns>The marker.</returns>
        private static string ResolveMarker(
            bool succeeded,
            bool completedWithIssues,
            bool failed)
        {
            return succeeded ? "PASS" : completedWithIssues ? "WARN" : failed ? "FAIL" : "MISS";
        }

        /// <summary>
        /// Formats a boolean as a public proof value.
        /// </summary>
        /// <param name="value">The boolean value.</param>
        /// <returns>The formatted value.</returns>
        private static string FormatBool(
            bool value)
        {
            return value ? "yes" : "no";
        }

        /// <summary>
        /// Resolves a public tenant display value.
        /// </summary>
        /// <param name="record">The ledger record.</param>
        /// <returns>The tenant display value.</returns>
        private static string ResolveTenantDisplay(
            CapturedIntegrationLedgerRecord record)
        {
            var tenant = TryGet(record, "tenant.id");
            if (!string.IsNullOrWhiteSpace(tenant))
            {
                return tenant;
            }

            return record.Category is AiDecisionLedgerCategory.RuntimeInstance or AiDecisionLedgerCategory.Queue or AiDecisionLedgerCategory.Recovery
                ? "infra"
                : "-";
        }

        /// <summary>
        /// Attempts to get a metadata value.
        /// </summary>
        /// <param name="record">The ledger record.</param>
        /// <param name="key">The metadata key.</param>
        /// <returns>The metadata value, or <c>null</c> when missing.</returns>
        private static string? TryGet(
            CapturedIntegrationLedgerRecord record,
            string key)
        {
            return record.Metadata.TryGetValue(key, out var value) ? value : null;
        }
    }
}
