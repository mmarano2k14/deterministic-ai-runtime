using System;
using System.Linq;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Tests the in-memory runtime lifecycle journal.
    /// </summary>
    public sealed class InMemoryAiRuntimeLifecycleJournalTests
    {
        [Fact]
        public async Task AppendAsync_Should_Preserve_Chronological_Order_And_All_FirstClass_Identities()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var timestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(CreateEvent("event-2", timestamp.AddSeconds(1)));
            await journal.AppendAsync(CreateEvent("event-1", timestamp));

            var events = await journal.ListByControlPlaneIdAsync("control-plane-1");

            Assert.Equal(new[] { "event-1", "event-2" }, events.Select(x => x.EventId));

            var lifecycleEvent = events[0];

            Assert.Equal(AiRuntimeHostCreationMode.KubernetesPool, lifecycleEvent.HostCreationMode);
            Assert.Equal("grpc", lifecycleEvent.ProviderName);
            Assert.Equal("pool-1", lifecycleEvent.PoolId);
            Assert.Equal("pod-uid-1", lifecycleEvent.HostId);
            Assert.Equal("pod-uid-1", lifecycleEvent.KubernetesPodUid);
            Assert.Equal("runtime-1", lifecycleEvent.RuntimeInstanceId);
            Assert.Equal("tenant-1", lifecycleEvent.TenantId);
            Assert.Equal("shared-run-1", lifecycleEvent.SharedRunId);
            Assert.Equal("execution-1", lifecycleEvent.ExecutionId);
            Assert.Equal("incident-1", lifecycleEvent.RuntimeFailureIncidentId);
            Assert.Equal("ledger-1", lifecycleEvent.LedgerEntryId);
            Assert.Equal("forensics-1", lifecycleEvent.ForensicsId);
        }

        [Fact]
        public async Task AppendAsync_Should_Be_Idempotent_For_Equivalent_EventId()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var lifecycleEvent = CreateEvent("event-idempotent", DateTimeOffset.UtcNow);

            await journal.AppendAsync(lifecycleEvent);
            await journal.AppendAsync(lifecycleEvent with
            {
                Metadata = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["provider.message"] = "created"
                }
            });

            var events = await journal.ListByControlPlaneIdAsync("control-plane-1");

            Assert.Single(events);
        }

        [Fact]
        public async Task AppendAsync_Should_Reject_Conflicting_EventId()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var lifecycleEvent = CreateEvent("event-conflict", DateTimeOffset.UtcNow);

            await journal.AppendAsync(lifecycleEvent);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                journal.AppendAsync(lifecycleEvent with
                {
                    RuntimeInstanceId = "runtime-conflict"
                }));

            Assert.Contains("different immutable payload", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ListBySharedRunIdAsync_Should_Enforce_Tenant_Scope()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var timestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(CreateEvent("event-tenant-a", timestamp));
            await journal.AppendAsync(CreateEvent("event-tenant-b", timestamp.AddSeconds(1)) with
            {
                TenantId = "tenant-2"
            });

            var events = await journal.ListBySharedRunIdAsync("tenant-1", "shared-run-1");

            Assert.Single(events);
            Assert.Equal("event-tenant-a", events[0].EventId);
        }

        [Fact]
        public async Task QueryMethods_Should_Return_The_Same_Correlated_Event()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var lifecycleEvent = CreateEvent("event-correlated", DateTimeOffset.UtcNow);

            await journal.AppendAsync(lifecycleEvent);

            Assert.Single(await journal.ListByPoolIdAsync("pool-1"));
            Assert.Single(await journal.ListByHostIdAsync("pod-uid-1"));
            Assert.Single(await journal.ListByKubernetesPodUidAsync("pod-uid-1"));
            Assert.Single(await journal.ListByRuntimeInstanceIdAsync("runtime-1"));
            Assert.Single(await journal.ListByRuntimeFailureIncidentIdAsync("incident-1"));
            Assert.Single(await journal.ListByExecutionIdAsync("execution-1"));
            Assert.Single(await journal.ListByCorrelationIdAsync("correlation-1"));
        }

        private static AiRuntimeLifecycleEvent CreateEvent(
            string eventId,
            DateTimeOffset timestampUtc)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = AiRuntimeLifecycleEventType.WorkReassigned,
                TimestampUtc = timestampUtc,
                ControlPlaneId = "control-plane-1",
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                ProviderName = "grpc",
                PoolId = "pool-1",
                HostId = "pod-uid-1",
                KubernetesPodUid = "pod-uid-1",
                KubernetesNamespace = "ai-runtime",
                KubernetesPodName = "runtime-pool-pod-1",
                KubernetesNodeName = "minikube",
                RuntimeInstanceId = "runtime-1",
                RuntimeId = "runtime-local-1",
                ProcessId = 42,
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                SharedRunId = "shared-run-1",
                LocalRunId = "local-run-1",
                ExecutionId = "execution-1",
                RuntimeFailureIncidentId = "incident-1",
                LedgerEntryId = "ledger-1",
                ForensicsId = "forensics-1",
                CorrelationId = "correlation-1",
                CausationId = "causation-1",
                PreviousStatus = "unhealthy",
                CurrentStatus = "ready",
                Reason = "runtime-replacement",
                Metadata = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["provider.message"] = "created"
                }
            };
        }
    }
}
