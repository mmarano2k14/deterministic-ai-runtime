using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Validates centralized projection to the existing append-once runtime lifecycle journal.
    /// </summary>
    public sealed class RuntimeLifecycleJournalAiControlPlaneEventSinkTests
    {
        [Fact]
        public async Task RecordAsync_Should_Preserve_Existing_Lifecycle_Payload_And_Remain_AppendOnce()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var sink = new RuntimeLifecycleJournalAiControlPlaneEventSink(journal);
            var lifecycleEvent = CreateLifecycleEvent();
            var engineEvent = AiRuntimeLifecycleEngineEventFactory.Create(lifecycleEvent);

            await sink.RecordAsync(engineEvent);
            await sink.RecordAsync(engineEvent);

            var stored = await journal.GetByEventIdAsync(lifecycleEvent.EventId);
            var byRuntime = await journal.ListByRuntimeInstanceIdAsync(lifecycleEvent.RuntimeInstanceId!);

            Assert.NotNull(stored);
            Assert.Single(byRuntime);
            Assert.Equal(lifecycleEvent.EventId, stored!.EventId);
            Assert.Equal(lifecycleEvent.EventType, stored.EventType);
            Assert.Equal(lifecycleEvent.TimestampUtc, stored.TimestampUtc);
            Assert.Equal(lifecycleEvent.ControlPlaneId, stored.ControlPlaneId);
            Assert.Equal(lifecycleEvent.HostCreationMode, stored.HostCreationMode);
            Assert.Equal(lifecycleEvent.ProviderName, stored.ProviderName);
            Assert.Equal(lifecycleEvent.PoolId, stored.PoolId);
            Assert.Equal(lifecycleEvent.HostId, stored.HostId);
            Assert.Equal(lifecycleEvent.KubernetesPodUid, stored.KubernetesPodUid);
            Assert.Equal(lifecycleEvent.KubernetesNamespace, stored.KubernetesNamespace);
            Assert.Equal(lifecycleEvent.KubernetesPodName, stored.KubernetesPodName);
            Assert.Equal(lifecycleEvent.KubernetesNodeName, stored.KubernetesNodeName);
            Assert.Equal(lifecycleEvent.RuntimeInstanceId, stored.RuntimeInstanceId);
            Assert.Equal(lifecycleEvent.RuntimeId, stored.RuntimeId);
            Assert.Equal(lifecycleEvent.ProcessId, stored.ProcessId);
            Assert.Equal(lifecycleEvent.TenantId, stored.TenantId);
            Assert.Equal(lifecycleEvent.TenantGroupId, stored.TenantGroupId);
            Assert.Equal(lifecycleEvent.SharedRunId, stored.SharedRunId);
            Assert.Equal(lifecycleEvent.LocalRunId, stored.LocalRunId);
            Assert.Equal(lifecycleEvent.ExecutionId, stored.ExecutionId);
            Assert.Equal(lifecycleEvent.RuntimeFailureIncidentId, stored.RuntimeFailureIncidentId);
            Assert.Equal(lifecycleEvent.LedgerEntryId, stored.LedgerEntryId);
            Assert.Equal(lifecycleEvent.ForensicsId, stored.ForensicsId);
            Assert.Equal(lifecycleEvent.CorrelationId, stored.CorrelationId);
            Assert.Equal(lifecycleEvent.CausationId, stored.CausationId);
            Assert.Equal(lifecycleEvent.PreviousStatus, stored.PreviousStatus);
            Assert.Equal(lifecycleEvent.CurrentStatus, stored.CurrentStatus);
            Assert.Equal(lifecycleEvent.Reason, stored.Reason);
            Assert.Equal("value", stored.Metadata["diagnostic"]);
        }


        [Fact]
        public async Task RecordAsync_Should_Ignore_Legacy_ControlPlane_Event_Without_Semantic_Event_Type()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var sink = new RuntimeLifecycleJournalAiControlPlaneEventSink(journal);
            var legacyEvent = new Multiplexed.Abstractions.AI.ControlPlane.Observability.Events.AiControlPlaneEvent
            {
                EventType = Multiplexed.Abstractions.AI.ControlPlane.Observability.Events.AiControlPlaneEventType.OperationCompleted,
                Area = Multiplexed.Abstractions.AI.ControlPlane.Observability.Area.AiControlPlaneArea.ExecutionControl,
                Operation = "list-runs",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new Multiplexed.Abstractions.AI.Observability.Context.AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "legacy-control-plane-event"
                }
            };

            await sink.RecordAsync(legacyEvent);

            Assert.Null(await journal.GetByEventIdAsync(legacyEvent.EventId));
        }

        [Fact]
        public async Task RecordAsync_Should_Reject_Envelope_Whose_Projection_Payload_Has_Different_EventType()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var sink = new RuntimeLifecycleJournalAiControlPlaneEventSink(journal);
            var lifecycleEvent = CreateLifecycleEvent();
            var engineEvent = AiRuntimeLifecycleEngineEventFactory.Create(lifecycleEvent);

            var mismatched = new Multiplexed.Abstractions.AI.ControlPlane.Observability.Events.AiControlPlaneEvent
            {
                EventId = engineEvent.EventId,
                SemanticEventType = AiRuntimeLifecycleEvents.RuntimeReady,
                EventType = engineEvent.EventType,
                Area = engineEvent.Area,
                Operation = engineEvent.Operation,
                Outcome = engineEvent.Outcome,
                Correlation = engineEvent.Correlation,
                TimestampUtc = engineEvent.TimestampUtc,
                Properties = engineEvent.Properties
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => sink.RecordAsync(mismatched));
            Assert.Null(await journal.GetByEventIdAsync(lifecycleEvent.EventId));
        }

        private static AiRuntimeLifecycleEvent CreateLifecycleEvent()
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = "runtime.registered:lifecycle:runtime-instance-1",
                EventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                TimestampUtc = new DateTimeOffset(2026, 8, 22, 5, 0, 0, TimeSpan.Zero),
                ControlPlaneId = "control-plane-1",
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                ProviderName = "kubernetes",
                PoolId = "pool-1",
                HostId = "host-1",
                KubernetesPodUid = "pod-uid-1",
                KubernetesNamespace = "runtime",
                KubernetesPodName = "runtime-pod-1",
                KubernetesNodeName = "node-1",
                RuntimeInstanceId = "runtime-instance-1",
                RuntimeId = "runtime-1",
                ProcessId = 42,
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                SharedRunId = "shared-run-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                RuntimeFailureIncidentId = "incident-1",
                LedgerEntryId = "ledger-entry-1",
                ForensicsId = "forensics-1",
                CorrelationId = "correlation-1",
                CausationId = "causation-1",
                PreviousStatus = "starting",
                CurrentStatus = "ready",
                Reason = "registered",
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["diagnostic"] = "value"
                }
            };
        }
    }
}
