using Multiplexed.Abstractions.AI.Observability.Ledger;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Validates reusable control-plane causal-chain ledger evidence for production runtime recovery scenarios.
    /// </summary>
    public static class ProductionControlPlaneLedgerCausalChainProof
    {
        /// <summary>
        /// Validates that the control-plane ledger contains the full recovery causal chain.
        /// </summary>
        /// <param name="ledgerEntries">The scenario-scoped ledger entries.</param>
        /// <param name="expectedRecoveredWorkCount">The expected recovered work count.</param>
        /// <param name="actualRecoveredWorkCount">The actual recovered work count.</param>
        /// <param name="failedRuntimeUnsafeValidated">Whether failed runtime unsafe state was already validated directly from registry state.</param>
        /// <returns>The causal-chain proof result.</returns>
        public static ProductionControlPlaneLedgerCausalChainProofResult Validate(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries,
            int expectedRecoveredWorkCount,
            int actualRecoveredWorkCount,
            bool failedRuntimeUnsafeValidated)
        {
            ArgumentNullException.ThrowIfNull(ledgerEntries);

            var scaleOutRequestPersistedCount =
                CountDistinctSuccessfulScaleOutPersistedRequests(
                    ledgerEntries);

            var scaleOutWatcherObservedCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-scale-out-request-watch",
                    CreateScaleOutCorrelationKey);

            var providerSelectedCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-scale-out-provider-selection",
                    CreateScaleOutCorrelationKey);

            var runtimeHostCreationCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-host-creation",
                    CreateRuntimeInstanceProofKey);

            var processRuntimeHostCreationCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-process-host-creation",
                    CreateRuntimeInstanceProofKey);

            var effectiveRuntimeHostCreationCount =
                Math.Max(
                    runtimeHostCreationCount,
                    processRuntimeHostCreationCount);

            var runtimeCapacityVisibleCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-capacity-get",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-capacity-publish",
                    CreateRuntimeInstanceProofKey);

            var runtimeRegistryVisibleCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-get",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-list",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-register",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-capacity-get",
                    CreateRuntimeInstanceProofKey);

            var failedRuntimeMarkedUnhealthyCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-mark-unhealthy",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-mark-unsafe",
                    CreateRuntimeInstanceProofKey) +
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "runtime-instance-suppress-capacity",
                    CreateRuntimeInstanceProofKey);

            var executionRecoveryReconciledCount =
                CountDistinctScenarioRecoveryEvidence(
                    ledgerEntries);

            var recoveredWorkRedispatchedCount =
                CountDistinctSuccessfulEvents(
                    ledgerEntries,
                    "remote-shared-run-dispatch",
                    CreateRunProofKey);

            var result =
                new ProductionControlPlaneLedgerCausalChainProofResult(
                    expectedRecoveredWorkCount,
                    actualRecoveredWorkCount,
                    scaleOutRequestPersistedCount,
                    scaleOutWatcherObservedCount,
                    providerSelectedCount,
                    effectiveRuntimeHostCreationCount,
                    processRuntimeHostCreationCount,
                    runtimeCapacityVisibleCount,
                    runtimeRegistryVisibleCount,
                    failedRuntimeMarkedUnhealthyCount,
                    failedRuntimeUnsafeValidated,
                    executionRecoveryReconciledCount,
                    recoveredWorkRedispatchedCount);

            Assert.Equal(expectedRecoveredWorkCount, actualRecoveredWorkCount);
            Assert.True(result.ScaleOutRequestPersistedCount > 0, "Control-plane ledger proof missing distinct successful scale-out request persisted evidence.");
            Assert.True(result.ScaleOutWatcherObservedCount > 0, "Control-plane ledger proof missing distinct successful scale-out watcher evidence.");
            Assert.True(result.ProviderSelectedCount > 0, "Control-plane ledger proof missing distinct successful provider selection evidence.");
            Assert.True(result.RuntimeHostCreatedCount > 0, "Control-plane ledger proof missing distinct successful runtime host creation evidence.");
            Assert.True(result.ProcessRuntimeHostStartedCount > 0, "Control-plane ledger proof missing distinct successful process runtime host creation evidence.");
            Assert.True(result.RuntimeCapacityVisibleCount > 0, "Control-plane ledger proof missing distinct successful runtime capacity visibility evidence.");
            Assert.True(result.RuntimeRegistryVisibleCount > 0, "Control-plane ledger proof missing distinct successful runtime registry visibility evidence.");
            Assert.True(result.FailedRuntimeUnsafeValidated, $"Control-plane proof missing failed runtime unsafe validation. LedgerUnhealthyRecords='{result.FailedRuntimeMarkedUnhealthyCount}', DirectRegistryUnsafeValidated='{result.DirectFailedRuntimeUnsafeValidated}'.");
            Assert.True(result.ExecutionRecoveryReconciledCount > 0, "Control-plane ledger proof missing distinct scenario recovery evidence.");
            Assert.True(result.RecoveredWorkRedispatchedCount > 0, "Control-plane ledger proof missing distinct successful recovered work redispatch evidence.");

            return result;
        }

        /// <summary>
        /// Validates that the control-plane ledger contains the full recovery causal chain.
        /// </summary>
        /// <param name="ledgerEntries">The scenario-scoped ledger entries.</param>
        /// <param name="expectedRecoveredWorkCount">The expected recovered work count.</param>
        /// <param name="actualRecoveredWorkCount">The actual recovered work count.</param>
        /// <returns>The causal-chain proof result.</returns>
        public static ProductionControlPlaneLedgerCausalChainProofResult Validate(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries,
            int expectedRecoveredWorkCount,
            int actualRecoveredWorkCount)
        {
            return Validate(
                ledgerEntries,
                expectedRecoveredWorkCount,
                actualRecoveredWorkCount,
                failedRuntimeUnsafeValidated: false);
        }

        private static int CountDistinctSuccessfulScaleOutPersistedRequests(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries)
        {
            var persistedCount =
                CountDistinctEntries(
                    ledgerEntries,
                    entry =>
                        IsSuccessfulEvent(entry, "runtime-scale-out-request-publish") &&
                        MetadataEquals(entry, "message", "Scale-out request persisted."),
                    CreateScaleOutRequestProofKey);

            if (persistedCount > 0)
            {
                return persistedCount;
            }

            return CountDistinctEntries(
                ledgerEntries,
                entry => IsSuccessfulEvent(entry, "runtime-scale-out-request-publish"),
                CreateScaleOutRequestProofKey);
        }

        private static int CountDistinctScenarioRecoveryEvidence(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries)
        {
            var scenarioRecoveryEvidenceCount =
                CountDistinctEntries(
                    ledgerEntries,
                    IsScenarioRecoveryEvidence,
                    CreateRecoveryProofKey);

            if (scenarioRecoveryEvidenceCount > 0)
            {
                return scenarioRecoveryEvidenceCount;
            }

            return CountDistinctSuccessfulEvents(
                ledgerEntries,
                "runtime-execution-recovery-reconcile",
                CreateRecoveryProofKey);
        }

        private static bool IsScenarioRecoveryEvidence(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return MetadataHasValue(
                    entry,
                    "recovery.forensicsId",
                    "property.recovery.forensicsId",
                    "scaleout.recovery.forensicsId",
                    "property.scaleout.recovery.forensicsId") ||
                MetadataHasValue(
                    entry,
                    "recovery.mode",
                    "property.recovery.mode",
                    "scaleout.recovery.mode",
                    "property.scaleout.recovery.mode") ||
                MetadataHasValue(
                    entry,
                    "recovery.reason",
                    "property.recovery.reason",
                    "scaleout.recovery.reason",
                    "property.scaleout.recovery.reason") ||
                MetadataHasValue(
                    entry,
                    "recovery.failedRuntimeInstanceId",
                    "property.recovery.failedRuntimeInstanceId",
                    "scaleout.recovery.failedRuntimeInstanceId",
                    "property.scaleout.recovery.failedRuntimeInstanceId",
                    "failed.runtimeInstanceId",
                    "property.failed.runtimeInstanceId") ||
                MetadataHasValue(
                    entry,
                    "recovery.failedLocalRunId",
                    "property.recovery.failedLocalRunId",
                    "scaleout.recovery.failedLocalRunId",
                    "property.scaleout.recovery.failedLocalRunId",
                    "failed.localRunId",
                    "property.failed.localRunId");
        }

        private static int CountDistinctSuccessfulEvents(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries,
            string eventTypeToken,
            Func<AiDecisionLedgerEntry, string> keySelector)
        {
            var succeededCount =
                CountDistinctEntries(
                    ledgerEntries,
                    entry => IsSuccessfulEvent(entry, eventTypeToken),
                    keySelector);

            if (succeededCount > 0)
            {
                return succeededCount;
            }

            return CountDistinctEntries(
                ledgerEntries,
                entry => EventTypeContains(entry, eventTypeToken),
                keySelector);
        }

        private static int CountDistinctEntries(
            IReadOnlyCollection<AiDecisionLedgerEntry> ledgerEntries,
            Func<AiDecisionLedgerEntry, bool> predicate,
            Func<AiDecisionLedgerEntry, string> keySelector)
        {
            return ledgerEntries
                .Where(predicate)
                .GroupBy(keySelector, StringComparer.Ordinal)
                .Count();
        }

        private static bool IsSuccessfulEvent(
            AiDecisionLedgerEntry entry,
            string eventTypeToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventTypeToken);

            return EventTypeContains(entry, eventTypeToken) &&
                (entry.EventType.EndsWith(".succeeded", StringComparison.Ordinal) ||
                entry.EventType.EndsWith(".operationcompleted", StringComparison.Ordinal) ||
                string.Equals(entry.Outcome.ToString(), "Succeeded", StringComparison.OrdinalIgnoreCase) ||
                MetadataEquals(entry, "outcome", "Succeeded"));
        }

        private static bool EventTypeContains(
            AiDecisionLedgerEntry entry,
            string eventTypeToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventTypeToken);

            return !string.IsNullOrWhiteSpace(entry.EventType) &&
                entry.EventType.Contains(eventTypeToken, StringComparison.Ordinal);
        }

        private static bool MetadataEquals(
            AiDecisionLedgerEntry entry,
            string key,
            string expectedValue)
        {
            var value =
                GetMetadataValue(
                    entry,
                    key,
                    $"property.{key}");

            return string.Equals(value, expectedValue, StringComparison.Ordinal);
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

        private static string CreateScaleOutRequestProofKey(
            AiDecisionLedgerEntry entry)
        {
            return FirstNonEmpty(
                GetMetadataValue(
                    entry,
                    "publishedScaleOutRequestId",
                    "property.publishedScaleOutRequestId",
                    "scaleOutRequestId",
                    "property.scaleOutRequestId",
                    "scaleout.requestId",
                    "property.scaleout.requestId",
                    "scaleout.scaleout.requestId",
                    "property.scaleout.scaleout.requestId"),
                entry.CorrelationContext.RunId,
                entry.CorrelationContext.CorrelationId,
                entry.EntryId,
                CreateLedgerFallbackKey(entry));
        }

        private static string CreateScaleOutCorrelationKey(
            AiDecisionLedgerEntry entry)
        {
            return FirstNonEmpty(
                GetMetadataValue(
                    entry,
                    "scaleOutRequestId",
                    "property.scaleOutRequestId",
                    "publishedScaleOutRequestId",
                    "property.publishedScaleOutRequestId",
                    "scaleout.requestId",
                    "property.scaleout.requestId",
                    "scaleout.scaleout.requestId",
                    "property.scaleout.scaleout.requestId"),
                entry.CorrelationContext.RunId,
                entry.CorrelationContext.CorrelationId,
                entry.EntryId,
                CreateLedgerFallbackKey(entry));
        }

        private static string CreateRuntimeInstanceProofKey(
            AiDecisionLedgerEntry entry)
        {
            return FirstNonEmpty(
                entry.CorrelationContext.RuntimeInstanceId,
                GetMetadataValue(
                    entry,
                    "runtimeInstanceId",
                    "property.runtimeInstanceId",
                    "runtime.instance.id",
                    "property.runtime.instance.id",
                    "scaleOutRuntimeInstanceId",
                    "property.scaleOutRuntimeInstanceId",
                    "scaleout.runtimeInstanceId",
                    "property.scaleout.runtimeInstanceId",
                    "scaleout.runtime.instance.id",
                    "property.scaleout.runtime.instance.id",
                    "failed.runtimeInstanceId",
                    "property.failed.runtimeInstanceId",
                    "recovery.failedRuntimeInstanceId",
                    "property.recovery.failedRuntimeInstanceId"),
                entry.CorrelationContext.CorrelationId,
                entry.EntryId,
                CreateLedgerFallbackKey(entry));
        }

        private static string CreateRecoveryProofKey(
            AiDecisionLedgerEntry entry)
        {
            return FirstNonEmpty(
                GetMetadataValue(
                    entry,
                    "recovery.forensicsId",
                    "property.recovery.forensicsId",
                    "scaleout.recovery.forensicsId",
                    "property.scaleout.recovery.forensicsId"),
                GetMetadataValue(
                    entry,
                    "recovery.failedExecutionId",
                    "property.recovery.failedExecutionId",
                    "scaleout.recovery.failedExecutionId",
                    "property.scaleout.recovery.failedExecutionId",
                    "executionId",
                    "property.executionId",
                    "execution.id",
                    "property.execution.id"),
                GetMetadataValue(
                    entry,
                    "recovery.failedLocalRunId",
                    "property.recovery.failedLocalRunId",
                    "scaleout.recovery.failedLocalRunId",
                    "property.scaleout.recovery.failedLocalRunId",
                    "failed.localRunId",
                    "property.failed.localRunId"),
                entry.CorrelationContext.ExecutionId,
                entry.CorrelationContext.RunId,
                entry.CorrelationContext.CorrelationId,
                entry.EntryId,
                CreateLedgerFallbackKey(entry));
        }

        private static string CreateRunProofKey(
            AiDecisionLedgerEntry entry)
        {
            return FirstNonEmpty(
                entry.CorrelationContext.RunId,
                GetMetadataValue(
                    entry,
                    "sharedRunId",
                    "property.sharedRunId",
                    "shared.run.id",
                    "property.shared.run.id",
                    "scaleout.sharedRunId",
                    "property.scaleout.sharedRunId",
                    "scaleout.shared.run.id",
                    "property.scaleout.shared.run.id"),
                entry.CorrelationContext.CorrelationId,
                entry.EntryId,
                CreateLedgerFallbackKey(entry));
        }

        private static string GetMetadataValue(
            AiDecisionLedgerEntry entry,
            params string[] keys)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(keys);

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

        private static string CreateLedgerFallbackKey(
            AiDecisionLedgerEntry entry)
        {
            return string.Join(
                "|",
                entry.EventType ?? string.Empty,
                entry.TimestampUtc.ToString("O"),
                entry.Sequence.ToString());
        }
    }
}