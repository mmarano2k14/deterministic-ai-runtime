using System;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    public sealed class AiRuntimeLifecycleEventWriterTests
    {
        [Fact]
        public async Task ResolveContextAsync_Should_Reconstruct_Infrastructure_From_Durable_History()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var writer = new AiRuntimeLifecycleEventWriter(journal);
            var timestamp = DateTimeOffset.UtcNow;

            await journal.AppendAsync(
                new AiRuntimeLifecycleEvent
                {
                    EventId = "runtime.registered:runtime-1",
                    EventType = AiRuntimeLifecycleEventType.RuntimeRegistered,
                    TimestampUtc = timestamp,
                    ControlPlaneId = "control-plane-1",
                    HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                    ProviderName = "grpc",
                    PoolId = "pool-1",
                    HostId = "pod-uid-1",
                    KubernetesPodUid = "pod-uid-1",
                    KubernetesNamespace = "runtime-system",
                    KubernetesPodName = "runtime-pool-1",
                    KubernetesNodeName = "node-1",
                    RuntimeInstanceId = "runtime-1",
                    RuntimeId = "child-1",
                    ProcessId = 42,
                    CorrelationId = "creation-1"
                });

            await journal.AppendAsync(
                new AiRuntimeLifecycleEvent
                {
                    EventId = "runtime.suppressed:runtime-1",
                    EventType = AiRuntimeLifecycleEventType.RuntimeSuppressed,
                    TimestampUtc = timestamp.AddSeconds(1),
                    ControlPlaneId = "control-plane-1",
                    PoolId = "pool-1",
                    HostId = "pod-uid-1",
                    RuntimeInstanceId = "runtime-1",
                    RuntimeFailureIncidentId = "failure-1"
                });

            var context = await writer.ResolveContextAsync(
                "runtime-1",
                "pod-uid-1",
                "pool-1");

            Assert.Equal("control-plane-1", context.ControlPlaneId);
            Assert.Equal(AiRuntimeHostCreationMode.KubernetesPool, context.HostCreationMode);
            Assert.Equal("grpc", context.ProviderName);
            Assert.Equal("pod-uid-1", context.KubernetesPodUid);
            Assert.Equal("runtime-system", context.KubernetesNamespace);
            Assert.Equal("runtime-pool-1", context.KubernetesPodName);
            Assert.Equal("node-1", context.KubernetesNodeName);
            Assert.Equal("child-1", context.RuntimeId);
            Assert.Equal(42, context.ProcessId);
        }

        [Fact]
        public async Task AppendOnceAsync_Should_Not_Create_A_Duplicate_On_Retry()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var writer = new AiRuntimeLifecycleEventWriter(journal);
            var lifecycleEvent = new AiRuntimeLifecycleEvent
            {
                EventId = "runtime.suppressed:failure-1:runtime-1",
                EventType = AiRuntimeLifecycleEventType.RuntimeSuppressed,
                TimestampUtc = DateTimeOffset.UtcNow,
                ControlPlaneId = "control-plane-1",
                RuntimeInstanceId = "runtime-1",
                RuntimeFailureIncidentId = "failure-1"
            };

            await writer.AppendOnceAsync(lifecycleEvent);
            await writer.AppendOnceAsync(lifecycleEvent with
            {
                TimestampUtc = lifecycleEvent.TimestampUtc.AddSeconds(1)
            });

            var events = await journal.ListByRuntimeFailureIncidentIdAsync("failure-1");

            Assert.Single(events);
            Assert.Equal(lifecycleEvent.TimestampUtc, events[0].TimestampUtc);
        }
    }
}
