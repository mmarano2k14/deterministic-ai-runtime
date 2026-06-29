using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger
{
    /// <summary>
    /// Provides assertions for production control-plane ledger proof scenarios.
    /// </summary>
    public static class ProductionControlPlaneLedgerProofAssertions
    {
        /// <summary>
        /// Asserts that the ledger contains the control-plane visibility proof exercised by the scenario.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        public static void AssertScaleOutAndRuntimeVisibilityProof(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            Assert.NotNull(records);
            Assert.NotEmpty(records);

            AssertContainsEvent(
                records,
                "control.sharedqueue.shared-queue-pump-cycle.operationstarted");

            AssertContainsEvent(
                records,
                "control.instanceregistry.runtime-instance-list.operationstarted");

            AssertContainsEvent(
                records,
                "control.admission.runtime-admission-decision.operationstarted");

            if (ContainsOperation(records, "runtime-scale-out-request-publish") ||
                ContainsOperation(records, "runtime-scale-out-request-watch") ||
                ContainsOperation(records, "runtime-scale-out-provider-selection") ||
                ContainsOperation(records, "runtime-host-creation") ||
                ContainsOperation(records, "runtime-process-host-creation"))
            {
                AssertContainsCompletedOperation(
                    records,
                    "runtime-scale-out-request-publish");

                AssertContainsCompletedOperation(
                    records,
                    "runtime-scale-out-request-watch");

                AssertContainsCompletedOperation(
                    records,
                    "runtime-scale-out-provider-selection");

                AssertContainsCompletedOperation(
                    records,
                    "runtime-host-creation");

                AssertContainsCompletedOperation(
                    records,
                    "runtime-process-host-creation");
            }

            if (ContainsOperation(records, "runtime-instance-capacity-publish"))
            {
                AssertContainsCompletedOperation(
                    records,
                    "runtime-instance-capacity-publish");
            }

            if (ContainsOperation(records, "runtime-instance-register"))
            {
                AssertContainsCompletedOperation(
                    records,
                    "runtime-instance-register");
            }
        }

        /// <summary>
        /// Asserts that the ledger contains the concurrent recovery proof exercised by the scenario.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        public static void AssertConcurrentRecoveryProof(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records)
        {
            Assert.NotNull(records);
            Assert.NotEmpty(records);

            AssertContainsEvent(
                records,
                "control.sharedqueue.shared-queue-pump-cycle.operationstarted");

            Assert.Contains(
                records,
                record =>
                    string.Equals(record.Category.ToString(), "Recovery", StringComparison.OrdinalIgnoreCase) ||
                    record.EventType.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
                    record.EventType.Contains("recover", StringComparison.OrdinalIgnoreCase));

            Assert.Contains(
                records,
                record =>
                    string.Equals(record.Category.ToString(), "SharedController", StringComparison.OrdinalIgnoreCase) ||
                    record.EventType.Contains("sharedcontroller", StringComparison.OrdinalIgnoreCase) ||
                    record.EventType.Contains("sharedqueue", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Asserts that at least one ledger record carries the expected tenant identifier.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="tenantId">The expected tenant identifier.</param>
        public static void AssertContainsTenant(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string tenantId)
        {
            Assert.NotNull(records);
            Assert.False(string.IsNullOrWhiteSpace(tenantId));

            Assert.Contains(
                records,
                record =>
                    ContainsMetadataValue(record, "tenant.id", tenantId) ||
                    ContainsMetadataValue(record, "tenantId", tenantId) ||
                    ContainsMetadataValue(record, "property.tenantId", tenantId) ||
                    ContainsMetadataValue(record, "property.tenant.id", tenantId) ||
                    record.Metadata.Values.Any(value =>
                        string.Equals(value, tenantId, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Asserts that the ledger contains a specific event type.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="eventType">The expected event type.</param>
        private static void AssertContainsEvent(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string eventType)
        {
            Assert.Contains(
                records,
                record => string.Equals(record.EventType, eventType, StringComparison.Ordinal));
        }

        /// <summary>
        /// Asserts that the ledger contains a completed event for the supplied operation.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The expected operation name.</param>
        private static void AssertContainsCompletedOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            Assert.Contains(
                records,
                record =>
                    record.EventType.Contains($".{operation}.", StringComparison.Ordinal) &&
                    IsCompletedOutcome(record.Outcome.ToString()));
        }

        /// <summary>
        /// Determines whether the ledger contains an operation.
        /// </summary>
        /// <param name="records">The captured ledger records.</param>
        /// <param name="operation">The operation name.</param>
        /// <returns><c>true</c> when the operation exists; otherwise, <c>false</c>.</returns>
        private static bool ContainsOperation(
            IReadOnlyCollection<CapturedIntegrationLedgerRecord> records,
            string operation)
        {
            return records.Any(
                record => record.EventType.Contains($".{operation}.", StringComparison.Ordinal));
        }

        /// <summary>
        /// Determines whether an outcome is a completed terminal outcome.
        /// </summary>
        /// <param name="outcome">The outcome name.</param>
        /// <returns><c>true</c> when the outcome is terminal; otherwise, <c>false</c>.</returns>
        private static bool IsCompletedOutcome(
            string outcome)
        {
            return string.Equals(outcome, "Succeeded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "CompletedWithIssues", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "Denied", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether a metadata key contains the expected value.
        /// </summary>
        /// <param name="record">The captured ledger record.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="expectedValue">The expected metadata value.</param>
        /// <returns><c>true</c> when the metadata contains the expected value; otherwise, <c>false</c>.</returns>
        private static bool ContainsMetadataValue(
            CapturedIntegrationLedgerRecord record,
            string key,
            string expectedValue)
        {
            return record.Metadata.TryGetValue(key, out var value) &&
                string.Equals(value, expectedValue, StringComparison.Ordinal);
        }
    }
}
