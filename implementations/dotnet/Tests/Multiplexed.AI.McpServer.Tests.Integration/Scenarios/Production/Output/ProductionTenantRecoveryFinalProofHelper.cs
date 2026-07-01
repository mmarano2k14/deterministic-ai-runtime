using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Builds compact tenant-scoped recovery proof fields for final production scenario output.
    /// </summary>
    public static class ProductionTenantRecoveryFinalProofHelper
    {
        private const string ResumeExistingExecutionRecoveryMode = "resume-existing-execution";
        private const string RequeueLocalQueuedRunRecoveryMode = "requeue-local-queued-run";
        private const string InFlightResumeTimelineType = "in-flight-resume-recovery";
        private const string LocalQueuedRecoveryTimelineType = "local-queued-recovery";

        /// <summary>
        /// Builds final-proof recovery details for a tenant.
        /// </summary>
        /// <typeparam name="TRecoveredWork">The recovered work type.</typeparam>
        /// <typeparam name="TForensicsRecord">The forensics record type.</typeparam>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="recoveredWorks">The recovered works.</param>
        /// <param name="forensicsRecords">The recovery forensics records.</param>
        /// <param name="isInFlightExecution">Returns true when the recovered work was an in-flight execution resume.</param>
        /// <param name="getRuntimeFailureIncidentId">Returns the runtime failure incident identifier from a forensics record.</param>
        /// <param name="getForensicsId">Returns the forensics identifier from a forensics record.</param>
        /// <param name="getTimelineEventTypes">Returns the ordered forensics timeline event types from a forensics record.</param>
        /// <returns>The tenant final-proof recovery details.</returns>
        public static ProductionTenantRecoveryFinalProof Build<TRecoveredWork, TForensicsRecord>(
            string tenantId,
            IReadOnlyCollection<TRecoveredWork> recoveredWorks,
            IReadOnlyCollection<TForensicsRecord> forensicsRecords,
            Func<TRecoveredWork, bool> isInFlightExecution,
            Func<TForensicsRecord, string?> getRuntimeFailureIncidentId,
            Func<TForensicsRecord, string?> getForensicsId,
            Func<TForensicsRecord, IReadOnlyCollection<string>> getTimelineEventTypes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(recoveredWorks);
            ArgumentNullException.ThrowIfNull(forensicsRecords);
            ArgumentNullException.ThrowIfNull(isInFlightExecution);
            ArgumentNullException.ThrowIfNull(getRuntimeFailureIncidentId);
            ArgumentNullException.ThrowIfNull(getForensicsId);
            ArgumentNullException.ThrowIfNull(getTimelineEventTypes);

            var recoveryModes =
                recoveredWorks
                    .Select(work => GetRecoveryMode(work, isInFlightExecution))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var runtimeFailureIncidentIds =
                forensicsRecords
                    .Select(getRuntimeFailureIncidentId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var forensicsIds =
                forensicsRecords
                    .Select(getForensicsId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var forensicsTimelineTypes =
                forensicsRecords
                    .Select(record => GetForensicsTimelineType(getTimelineEventTypes(record)))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            return new ProductionTenantRecoveryFinalProof(
                tenantId,
                recoveredWorks.Count,
                forensicsRecords.Count,
                recoveryModes,
                runtimeFailureIncidentIds,
                forensicsIds,
                forensicsTimelineTypes);
        }

        /// <summary>
        /// Writes a compact tenant forensics timeline proof.
        /// </summary>
        /// <typeparam name="TForensicsRecord">The forensics record type.</typeparam>
        /// <param name="writeLine">The output writer.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="forensicsRecords">The forensics records.</param>
        /// <param name="getForensicsId">Returns the forensics identifier.</param>
        /// <param name="getExecutionId">Returns the execution identifier.</param>
        /// <param name="getSharedRunId">Returns the shared run identifier.</param>
        /// <param name="getTenantId">Returns the tenant identifier.</param>
        /// <param name="getRuntimeFailureIncidentId">Returns the runtime failure incident identifier.</param>
        /// <param name="getTimelineEventTypes">Returns the ordered timeline event types.</param>
        public static void WriteForensicsTimelineProof<TForensicsRecord>(
            Action<string> writeLine,
            string tenantId,
            IReadOnlyCollection<TForensicsRecord> forensicsRecords,
            Func<TForensicsRecord, string?> getForensicsId,
            Func<TForensicsRecord, string?> getExecutionId,
            Func<TForensicsRecord, string?> getSharedRunId,
            Func<TForensicsRecord, string?> getTenantId,
            Func<TForensicsRecord, string?> getRuntimeFailureIncidentId,
            Func<TForensicsRecord, IReadOnlyCollection<string>> getTimelineEventTypes)
        {
            ArgumentNullException.ThrowIfNull(writeLine);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(forensicsRecords);
            ArgumentNullException.ThrowIfNull(getForensicsId);
            ArgumentNullException.ThrowIfNull(getExecutionId);
            ArgumentNullException.ThrowIfNull(getSharedRunId);
            ArgumentNullException.ThrowIfNull(getTenantId);
            ArgumentNullException.ThrowIfNull(getRuntimeFailureIncidentId);
            ArgumentNullException.ThrowIfNull(getTimelineEventTypes);

            writeLine($"## FINAL FORENSICS TIMELINES - TenantId='{tenantId}', Count='{forensicsRecords.Count}'");

            var index = 1;

            foreach (var record in forensicsRecords.OrderBy(record => getForensicsId(record), StringComparer.Ordinal))
            {
                var timeline =
                    string.Join(
                        " -> ",
                        getTimelineEventTypes(record));

                writeLine(
                    $"{index:00}. ForensicsId='{getForensicsId(record)}', ExecutionId='{getExecutionId(record)}', SharedRunId='{getSharedRunId(record)}', TenantId='{getTenantId(record)}', RuntimeFailureIncidentId='{getRuntimeFailureIncidentId(record)}', Timeline='{timeline}'.");

                index++;
            }
        }

        /// <summary>
        /// Formats the recovery modes for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The formatted recovery modes.</returns>
        public static string FormatRecoveryModes(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return FormatValues(proof.RecoveryModes);
        }

        /// <summary>
        /// Formats the runtime failure incident identifiers for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The formatted runtime failure incident identifiers.</returns>
        public static string FormatRuntimeFailureIncidentIds(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return FormatValues(proof.RuntimeFailureIncidentIds);
        }

        /// <summary>
        /// Formats the forensics identifiers for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The formatted forensics identifiers.</returns>
        public static string FormatForensicsIds(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return FormatValues(proof.ForensicsIds);
        }

        /// <summary>
        /// Formats the forensics timeline types for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The formatted forensics timeline types.</returns>
        public static string FormatForensicsTimelineTypes(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return FormatValues(proof.ForensicsTimelineTypes);
        }

        /// <summary>
        /// Formats the recovered work count for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The recovered work count.</returns>
        public static string FormatRecoveredWorkCount(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return proof.RecoveredWorkCount.ToString();
        }

        /// <summary>
        /// Formats the forensics record count for a final proof line.
        /// </summary>
        /// <param name="proof">The tenant recovery proof.</param>
        /// <returns>The forensics record count.</returns>
        public static string FormatForensicsRecordCount(
            ProductionTenantRecoveryFinalProof proof)
        {
            ArgumentNullException.ThrowIfNull(proof);

            return proof.ForensicsRecordCount.ToString();
        }

        private static string GetRecoveryMode<TRecoveredWork>(
            TRecoveredWork recoveredWork,
            Func<TRecoveredWork, bool> isInFlightExecution)
        {
            ArgumentNullException.ThrowIfNull(isInFlightExecution);

            return isInFlightExecution(recoveredWork)
                ? ResumeExistingExecutionRecoveryMode
                : RequeueLocalQueuedRunRecoveryMode;
        }

        private static string GetForensicsTimelineType(
            IReadOnlyCollection<string> timelineEventTypes)
        {
            ArgumentNullException.ThrowIfNull(timelineEventTypes);

            if (timelineEventTypes.Any(eventType => string.Equals(eventType, "execution.recovery.candidate.detected", StringComparison.Ordinal)) ||
                timelineEventTypes.Any(eventType => string.Equals(eventType, "shared.run.requeued.for.resume", StringComparison.Ordinal)))
            {
                return InFlightResumeTimelineType;
            }

            if (timelineEventTypes.Any(eventType => string.Equals(eventType, "SharedRunRequeuedForLocalQueuedRecovery", StringComparison.Ordinal)) ||
                timelineEventTypes.Any(eventType => string.Equals(eventType, "failed.local.run.marked.requeued.for.recovery", StringComparison.Ordinal)))
            {
                return LocalQueuedRecoveryTimelineType;
            }

            return string.Empty;
        }

        private static string FormatValues(
            IReadOnlyCollection<string> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            return string.Join(",", values);
        }
    }

    /// <summary>
    /// Represents compact tenant recovery proof fields for final production scenario output.
    /// </summary>
    /// <param name="TenantId">The tenant identifier.</param>
    /// <param name="RecoveredWorkCount">The recovered work count.</param>
    /// <param name="ForensicsRecordCount">The forensics record count.</param>
    /// <param name="RecoveryModes">The distinct recovery modes.</param>
    /// <param name="RuntimeFailureIncidentIds">The distinct runtime failure incident identifiers.</param>
    /// <param name="ForensicsIds">The distinct forensics identifiers.</param>
    /// <param name="ForensicsTimelineTypes">The distinct forensics timeline types.</param>
    public sealed record ProductionTenantRecoveryFinalProof(
        string TenantId,
        int RecoveredWorkCount,
        int ForensicsRecordCount,
        IReadOnlyCollection<string> RecoveryModes,
        IReadOnlyCollection<string> RuntimeFailureIncidentIds,
        IReadOnlyCollection<string> ForensicsIds,
        IReadOnlyCollection<string> ForensicsTimelineTypes);
}
