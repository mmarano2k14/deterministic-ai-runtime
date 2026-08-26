using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests durable evidence rehydration used to close deterministic lifecycle observation races.
    /// </summary>
    public sealed class DurableAiDeterministicLifecycleEvidenceReaderTests
    {
        /// <summary>
        /// Verifies that an already persisted Runtime Lifecycle Journal fact is rehydrated as its canonical engine event.
        /// </summary>
        [Fact]
        public async Task FindAsync_Should_Rehydrate_Runtime_Lifecycle_Journal_Evidence()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var expectedTimestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(
                new AiRuntimeLifecycleEvent
                {
                    EventId = "runtime-event-1",
                    EventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                    TimestampUtc = expectedTimestamp,
                    ControlPlaneId = "control-plane-1",
                    RuntimeInstanceId = "runtime-1",
                    ExecutionId = "execution-1",
                    CorrelationId = "correlation-1"
                },
                CancellationToken.None).ConfigureAwait(false);

            var reader = CreateReader(lifecycleJournal: journal);

            var observed = await reader.FindAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                    ExecutionId = "execution-1",
                    RuntimeInstanceId = "runtime-1"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(observed);
            Assert.Equal("runtime-event-1", observed.EventId);
            Assert.Equal(AiRuntimeLifecycleEvents.RuntimeRegistered, observed.SemanticEventType);
            Assert.Equal("execution-1", observed.Correlation.ExecutionId);
            Assert.Equal("runtime-1", observed.Correlation.RuntimeInstanceId);
            Assert.Equal(expectedTimestamp, observed.TimestampUtc);
        }

        /// <summary>
        /// Verifies that an already persisted Recovery Forensics fact is rehydrated as its canonical recovery event.
        /// </summary>
        [Fact]
        public async Task FindAsync_Should_Rehydrate_Recovery_Forensics_Evidence()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.AppendEventAsync(
                "forensics-1",
                new AiRuntimeRecoveryForensicsEvent
                {
                    EventId = "recovery-event-1",
                    ForensicsId = "forensics-1",
                    TimestampUtc = DateTimeOffset.UtcNow,
                    EventType = AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                    Outcome = "Recovered",
                    ExecutionId = "execution-2",
                    SharedRunId = "shared-run-2",
                    RuntimeInstanceId = "runtime-2"
                },
                CancellationToken.None).ConfigureAwait(false);

            var reader = CreateReader(recoveryForensicsStore: store);

            var observed = await reader.FindAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                    ForensicsId = "forensics-1",
                    ExecutionId = "execution-2",
                    SharedRunId = "shared-run-2"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(observed);
            Assert.Equal("recovery-event-1", observed.EventId);
            Assert.Equal(AiEngineEvents.Recovery.ExecutionRecoveryCompleted, observed.SemanticEventType);
            Assert.Equal("forensics-1", observed.Correlation.CorrelationId);
            Assert.Equal("shared-run-2", observed.Correlation.RunId);
            Assert.Equal("execution-2", observed.Correlation.ExecutionId);
        }

        /// <summary>
        /// Verifies that an already persisted Decision Ledger fact is rehydrated using existing ledger correlation identities.
        /// </summary>
        [Fact]
        public async Task FindAsync_Should_Rehydrate_Decision_Ledger_Evidence()
        {
            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(
                new AiDecisionLedgerEntry
                {
                    EntryId = "ledger-entry-1",
                    CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                    {
                        ExecutionId = "execution-3",
                        RunId = "run-3",
                        RuntimeInstanceId = "runtime-3",
                        CorrelationId = "correlation-3",
                        Operation = AiEngineEvents.Policy.Allowed
                    },
                    Category = AiDecisionLedgerCategory.Policy,
                    EventType = AiEngineEvents.Policy.Allowed,
                    Outcome = AiDecisionLedgerOutcome.Allowed,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["event.id"] = "policy-event-1",
                        ["event.type"] = "OperationCompleted",
                        ["area"] = "Policy",
                        ["operation"] = AiEngineEvents.Policy.Allowed,
                        ["outcome"] = "Succeeded"
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            var reader = CreateReader(ledger: ledger);

            var observed = await reader.FindAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.Policy.Allowed,
                    EventId = "policy-event-1",
                    ExecutionId = "execution-3",
                    RunId = "run-3",
                    RuntimeInstanceId = "runtime-3",
                    CorrelationId = "correlation-3"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(observed);
            Assert.Equal("policy-event-1", observed.EventId);
            Assert.Equal(AiEngineEvents.Policy.Allowed, observed.SemanticEventType);
            Assert.Equal("execution-3", observed.Correlation.ExecutionId);
            Assert.Equal("run-3", observed.Correlation.RunId);
        }

        /// <summary>
        /// Verifies that a persisted Child DAG continuation-scheduled fact can be rehydrated by the exact
        /// indexed child execution/correlation identity while still enforcing parent-property filters.
        /// </summary>
        [Fact]
        public async Task FindAsync_Should_Rehydrate_Child_Continuation_Scheduled_By_Indexed_Identity()
        {
            const string childExecutionId = "child-execution-1";
            const string childInvocationKey = "child-invocation-1";
            const string parentExecutionId = "parent-execution-1";
            const string parentCallSiteId = "execute-child-dag";

            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(
                new AiDecisionLedgerEntry
                {
                    EntryId = "child-continuation-ledger-entry-1",
                    CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                    {
                        ExecutionId = childExecutionId,
                        CorrelationId = childInvocationKey,
                        Operation = AiEngineEvents.ChildDag.ContinuationScheduled
                    },
                    Category = AiDecisionLedgerCategory.Dag,
                    EventType = AiEngineEvents.ChildDag.ContinuationScheduled,
                    Outcome = AiDecisionLedgerOutcome.Succeeded,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["event.id"] =
                            $"{AiEngineEvents.ChildDag.ContinuationScheduled}:{childInvocationKey}",
                        ["event.type"] = "OperationCompleted",
                        ["area"] = "ChildDag",
                        ["operation"] = AiEngineEvents.ChildDag.ContinuationScheduled,
                        ["outcome"] = "Succeeded",
                        [AiChildDagMetadataKeys.InvocationKey] = childInvocationKey,
                        [AiChildDagMetadataKeys.ExecutionId] = childExecutionId,
                        [AiChildDagMetadataKeys.ParentExecutionId] = parentExecutionId,
                        [AiChildDagMetadataKeys.ParentCallSiteId] = parentCallSiteId
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            var reader = CreateReader(ledger: ledger);

            var observed = await reader.FindAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationScheduled,
                    EventId =
                        $"{AiEngineEvents.ChildDag.ContinuationScheduled}:{childInvocationKey}",
                    ExecutionId = childExecutionId,
                    CorrelationId = childInvocationKey,
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AiChildDagMetadataKeys.ParentExecutionId] = parentExecutionId,
                        [AiChildDagMetadataKeys.ParentCallSiteId] = parentCallSiteId
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.NotNull(observed);
            Assert.Equal(
                AiEngineEvents.ChildDag.ContinuationScheduled,
                observed.SemanticEventType);
            Assert.Equal(childExecutionId, observed.Correlation.ExecutionId);
            Assert.Equal(childInvocationKey, observed.Correlation.CorrelationId);
            Assert.Equal(
                parentExecutionId,
                observed.Properties[AiChildDagMetadataKeys.ParentExecutionId]);
            Assert.Equal(
                parentCallSiteId,
                observed.Properties[AiChildDagMetadataKeys.ParentCallSiteId]);
        }

        /// <summary>
        /// Verifies that the durable Ledger reader deliberately refuses a properties-only global scan.
        /// This documents why deterministic cross-process waits must include an existing indexed identity.
        /// </summary>
        [Fact]
        public async Task FindAsync_Should_Not_Global_Scan_Ledger_For_Properties_Only_Criteria()
        {
            var ledger = new InMemoryAiDecisionLedger();

            await ledger.AppendAsync(
                new AiDecisionLedgerEntry
                {
                    EntryId = "child-continuation-ledger-entry-2",
                    CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                    {
                        ExecutionId = "child-execution-2",
                        CorrelationId = "child-invocation-2",
                        Operation = AiEngineEvents.ChildDag.ContinuationScheduled
                    },
                    Category = AiDecisionLedgerCategory.Dag,
                    EventType = AiEngineEvents.ChildDag.ContinuationScheduled,
                    Outcome = AiDecisionLedgerOutcome.Succeeded,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AiChildDagMetadataKeys.ParentExecutionId] = "parent-execution-2",
                        [AiChildDagMetadataKeys.ParentCallSiteId] = "execute-child-dag"
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            var reader = CreateReader(ledger: ledger);

            var observed = await reader.FindAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationScheduled,
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [AiChildDagMetadataKeys.ParentExecutionId] = "parent-execution-2",
                        [AiChildDagMetadataKeys.ParentCallSiteId] = "execute-child-dag"
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Null(observed);
        }

        private static DurableAiDeterministicLifecycleEvidenceReader CreateReader(
            IAiDecisionLedger? ledger = null,
            IAiRuntimeLifecycleJournal? lifecycleJournal = null,
            IAiRuntimeRecoveryForensicsStore? recoveryForensicsStore = null)
        {
            return new DurableAiDeterministicLifecycleEvidenceReader(
                ledger is null ? Array.Empty<IAiDecisionLedger>() : new[] { ledger },
                lifecycleJournal is null ? Array.Empty<IAiRuntimeLifecycleJournal>() : new[] { lifecycleJournal },
                recoveryForensicsStore is null
                    ? Array.Empty<IAiRuntimeRecoveryForensicsStore>()
                    : new[] { recoveryForensicsStore });
        }
    }
}
