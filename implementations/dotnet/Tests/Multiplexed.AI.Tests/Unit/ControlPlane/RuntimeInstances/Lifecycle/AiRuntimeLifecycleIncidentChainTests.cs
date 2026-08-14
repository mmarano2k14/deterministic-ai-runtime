using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    public sealed class AiRuntimeLifecycleIncidentChainTests
    {
        [Fact]
        public async Task Incident_Query_Should_Join_Pod_Runtimes_Replacement_And_Reassigned_Work()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var timestamp = DateTimeOffset.UtcNow;
            var incidentId = "pod-failure-1";

            await journal.AppendAsync(CreateInfrastructureEvent(
                "host.disappeared",
                AiRuntimeLifecycleEventType.HostDisappeared,
                timestamp,
                incidentId,
                hostId: "pod-old",
                runtimeInstanceId: null));

            await journal.AppendAsync(CreateInfrastructureEvent(
                "runtime.unhealthy:runtime-1",
                AiRuntimeLifecycleEventType.RuntimeUnhealthy,
                timestamp.AddTicks(1),
                incidentId,
                hostId: "pod-old",
                runtimeInstanceId: "runtime-1"));

            await journal.AppendAsync(CreateInfrastructureEvent(
                "runtime.suppressed:runtime-1",
                AiRuntimeLifecycleEventType.RuntimeSuppressed,
                timestamp.AddTicks(2),
                incidentId,
                hostId: "pod-old",
                runtimeInstanceId: "runtime-1"));

            await journal.AppendAsync(CreateInfrastructureEvent(
                "runtime.replacement.requested:runtime-1",
                AiRuntimeLifecycleEventType.RuntimeReplacementRequested,
                timestamp.AddTicks(3),
                incidentId,
                hostId: "pod-old",
                runtimeInstanceId: "runtime-1"));

            await journal.AppendAsync(CreateInfrastructureEvent(
                "runtime.replacement.registered:runtime-4",
                AiRuntimeLifecycleEventType.RuntimeReplacementRegistered,
                timestamp.AddTicks(4),
                incidentId,
                hostId: "pod-new",
                runtimeInstanceId: "runtime-4"));

            await journal.AppendAsync(CreateWorkEvent(
                "work.released:local-1",
                AiRuntimeLifecycleEventType.WorkReleased,
                timestamp.AddTicks(5),
                incidentId,
                "runtime-1",
                "local-1",
                "execution-1"));

            await journal.AppendAsync(CreateWorkEvent(
                "work.reassigned:local-2",
                AiRuntimeLifecycleEventType.WorkReassigned,
                timestamp.AddTicks(6),
                incidentId,
                "runtime-4",
                "local-2",
                "execution-1"));

            var events = await journal.ListByRuntimeFailureIncidentIdAsync(incidentId);

            Assert.Equal(7, events.Count);
            Assert.Equal(
                new[]
                {
                    AiRuntimeLifecycleEventType.HostDisappeared,
                    AiRuntimeLifecycleEventType.RuntimeUnhealthy,
                    AiRuntimeLifecycleEventType.RuntimeSuppressed,
                    AiRuntimeLifecycleEventType.RuntimeReplacementRequested,
                    AiRuntimeLifecycleEventType.RuntimeReplacementRegistered,
                    AiRuntimeLifecycleEventType.WorkReleased,
                    AiRuntimeLifecycleEventType.WorkReassigned
                },
                events.Select(item => item.EventType));

            var reassigned = Assert.Single(events.Where(
                item => item.EventType == AiRuntimeLifecycleEventType.WorkReassigned));

            Assert.Equal("tenant-1", reassigned.TenantId);
            Assert.Equal("shared-run-1", reassigned.SharedRunId);
            Assert.Equal("execution-1", reassigned.ExecutionId);
            Assert.Equal("runtime-4", reassigned.RuntimeInstanceId);
            Assert.Equal("forensics-1", reassigned.ForensicsId);
        }

        [Fact]
        public async Task SharedRun_Query_Should_Not_Leak_Another_Tenant_During_Recovery()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var timestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(CreateWorkEvent(
                "work.reassigned:tenant-1",
                AiRuntimeLifecycleEventType.WorkReassigned,
                timestamp,
                "incident-1",
                "runtime-4",
                "local-2",
                "execution-1"));

            await journal.AppendAsync(CreateWorkEvent(
                "work.reassigned:tenant-2",
                AiRuntimeLifecycleEventType.WorkReassigned,
                timestamp.AddTicks(1),
                "incident-2",
                "runtime-safe",
                "local-safe",
                "execution-safe") with
                {
                    TenantId = "tenant-2"
                });

            var tenantEvents = await journal.ListBySharedRunIdAsync(
                "tenant-1",
                "shared-run-1");

            Assert.Single(tenantEvents);
            Assert.Equal("tenant-1", tenantEvents[0].TenantId);
            Assert.Equal("incident-1", tenantEvents[0].RuntimeFailureIncidentId);
        }

        private static AiRuntimeLifecycleEvent CreateInfrastructureEvent(
            string eventId,
            string eventType,
            DateTimeOffset timestampUtc,
            string incidentId,
            string hostId,
            string? runtimeInstanceId)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = "control-plane-1",
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                ProviderName = "grpc",
                PoolId = "pool-1",
                HostId = hostId,
                KubernetesPodUid = hostId,
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeFailureIncidentId = incidentId,
                CorrelationId = incidentId,
                Metadata = new Dictionary<string, string>()
            };
        }

        private static AiRuntimeLifecycleEvent CreateWorkEvent(
            string eventId,
            string eventType,
            DateTimeOffset timestampUtc,
            string incidentId,
            string runtimeInstanceId,
            string localRunId,
            string executionId)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = "control-plane-1",
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                ProviderName = "grpc",
                PoolId = "pool-1",
                HostId = runtimeInstanceId == "runtime-1" ? "pod-old" : "pod-new",
                KubernetesPodUid = runtimeInstanceId == "runtime-1" ? "pod-old" : "pod-new",
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                SharedRunId = "shared-run-1",
                LocalRunId = localRunId,
                ExecutionId = executionId,
                RuntimeFailureIncidentId = incidentId,
                ForensicsId = "forensics-1",
                CorrelationId = "forensics-1",
                Metadata = new Dictionary<string, string>()
            };
        }
    }
}
