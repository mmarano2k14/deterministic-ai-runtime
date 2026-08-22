using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Validates durable lifecycle instrumentation owned by the common host creation manager.
    /// </summary>
    public sealed class AiRuntimeHostCreationLifecycleJournalTests
    {
        /// <summary>
        /// Verifies that every supported host creation mode produces one non-duplicated creation chain.
        /// </summary>
        [Theory]
        [InlineData(AiRuntimeHostCreationMode.Fixture)]
        [InlineData(AiRuntimeHostCreationMode.Process)]
        [InlineData(AiRuntimeHostCreationMode.Kubernetes)]
        [InlineData(AiRuntimeHostCreationMode.Attach)]
        [InlineData(AiRuntimeHostCreationMode.KubernetesPool)]
        public async Task StartRuntimeAsync_Should_Append_One_Creation_Chain_For_Each_Mode(
            AiRuntimeHostCreationMode hostCreationMode)
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var strategy = new RecordingRuntimeHostCreationStrategy(hostCreationMode);
            var manager = CreateManager(strategy, journal);
            var request = CreateRequest(hostCreationMode);

            var result = await manager.StartRuntimeAsync(request);

            Assert.True(result.Success);

            var events = await journal.ListByCorrelationIdAsync(request.RequestId);

            Assert.Equal(
                new[]
                {
                    AiRuntimeLifecycleEvents.HostCreationRequested,
                    AiRuntimeLifecycleEvents.HostCreationStarted,
                    AiRuntimeLifecycleEvents.HostCreationSucceeded
                },
                events.Select(lifecycleEvent => lifecycleEvent.EventType));
            Assert.All(events, lifecycleEvent =>
            {
                Assert.Equal(request.ControlPlaneId, lifecycleEvent.ControlPlaneId);
                Assert.Equal(hostCreationMode, lifecycleEvent.HostCreationMode);
                Assert.Equal(request.RuntimeInstanceId, lifecycleEvent.RuntimeInstanceId);
                Assert.Equal(request.RequestId, lifecycleEvent.CorrelationId);
            });
            Assert.Null(events[0].CausationId);
            Assert.Equal(events[0].EventId, events[1].CausationId);
            Assert.Equal(events[1].EventId, events[2].CausationId);
            var strategyRequest = Assert.IsType<AiRuntimeHostStartRequest>(strategy.LastRequest);
            Assert.Equal(
                request.RequestId,
                strategyRequest.Metadata[AiRuntimeHostMetadataKeys.LifecycleCorrelationId]);
            Assert.Equal(
                hostCreationMode.ToString(),
                strategyRequest.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
        }

        /// <summary>
        /// Verifies that replaying the same host request does not duplicate its lifecycle chain.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Not_Duplicate_The_Same_Request_Chain()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var strategy = new RecordingRuntimeHostCreationStrategy(
                AiRuntimeHostCreationMode.Process);
            var manager = CreateManager(strategy, journal);
            var request = CreateRequest(AiRuntimeHostCreationMode.Process);

            await manager.StartRuntimeAsync(request);
            await manager.StartRuntimeAsync(request);

            var events = await journal.ListByCorrelationIdAsync(request.RequestId);

            Assert.Equal(3, events.Count);
            Assert.Single(events.Where(lifecycleEvent =>
                lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationRequested));
            Assert.Single(events.Where(lifecycleEvent =>
                lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationStarted));
            Assert.Single(events.Where(lifecycleEvent =>
                lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationSucceeded));
        }

        /// <summary>
        /// Verifies that a rejected creation produces no false started or succeeded event.
        /// </summary>
        [Fact]
        public async Task StartRuntimeAsync_Should_Append_Requested_And_Failed_When_Mode_Is_Not_Registered()
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var manager = new AiRuntimeHostCreationManager(
                new IAiRuntimeHostCreationStrategy[]
                {
                    new RecordingRuntimeHostCreationStrategy(AiRuntimeHostCreationMode.Process)
                },
                NullLogger<AiRuntimeHostCreationManager>.Instance,
                new NoopAiControlPlaneObserver(),
                journal);
            var request = CreateRequest(AiRuntimeHostCreationMode.Kubernetes);

            var result = await manager.StartRuntimeAsync(request);

            Assert.False(result.Success);

            var events = await journal.ListByCorrelationIdAsync(request.RequestId);

            Assert.Equal(2, events.Count);
            Assert.Equal(AiRuntimeLifecycleEvents.HostCreationRequested, events[0].EventType);
            Assert.Equal(AiRuntimeLifecycleEvents.HostCreationFailed, events[1].EventType);
            Assert.DoesNotContain(
                events,
                lifecycleEvent => lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationStarted);
            Assert.DoesNotContain(
                events,
                lifecycleEvent => lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationSucceeded);
        }

        /// <summary>
        /// Verifies that pooled infrastructure is not assigned to the first tenant that requests it.
        /// </summary>
        [Theory]
        [InlineData(AiRuntimeHostCreationMode.Process)]
        [InlineData(AiRuntimeHostCreationMode.KubernetesPool)]
        public async Task StartRuntimeAsync_Should_Keep_Pooled_Infrastructure_Tenant_Null(
            AiRuntimeHostCreationMode hostCreationMode)
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var strategy = new RecordingRuntimeHostCreationStrategy(hostCreationMode);
            var manager = CreateManager(strategy, journal);
            var request = CreateRequest(hostCreationMode) with
            {
                PoolId = "pool-a",
                HostId = hostCreationMode == AiRuntimeHostCreationMode.KubernetesPool
                    ? "pod-uid-a"
                    : "process-pool-host-a"
            };

            await manager.StartRuntimeAsync(request);

            var events = await journal.ListByCorrelationIdAsync(request.RequestId);

            Assert.NotEmpty(events);
            Assert.All(events, lifecycleEvent =>
            {
                Assert.Null(lifecycleEvent.TenantId);
                Assert.Null(lifecycleEvent.TenantGroupId);
                Assert.Equal("pool-a", lifecycleEvent.PoolId);
            });

            if (hostCreationMode == AiRuntimeHostCreationMode.KubernetesPool)
            {
                Assert.All(events, lifecycleEvent =>
                    Assert.Equal("pod-uid-a", lifecycleEvent.KubernetesPodUid));
            }
        }

        /// <summary>
        /// Verifies that provider-enriched Kubernetes identities are promoted to typed fields.
        /// </summary>
        [Theory]
        [InlineData(AiRuntimeHostCreationMode.Kubernetes)]
        [InlineData(AiRuntimeHostCreationMode.KubernetesPool)]
        public async Task StartRuntimeAsync_Should_Map_Kubernetes_Identity_To_Typed_Fields(
            AiRuntimeHostCreationMode hostCreationMode)
        {
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var strategy = new RecordingRuntimeHostCreationStrategy(hostCreationMode);
            var manager = CreateManager(strategy, journal);
            var request = CreateRequest(hostCreationMode);

            await manager.StartRuntimeAsync(request);

            var events = await journal.ListByCorrelationIdAsync(request.RequestId);
            var succeeded = Assert.Single(
                events.Where(lifecycleEvent =>
                    lifecycleEvent.EventType == AiRuntimeLifecycleEvents.HostCreationSucceeded));

            Assert.Equal("pod-uid-a", succeeded.HostId);
            Assert.Equal("pod-uid-a", succeeded.KubernetesPodUid);
            Assert.Equal("runtime-tests", succeeded.KubernetesNamespace);
            Assert.Equal("runtime-pod-a", succeeded.KubernetesPodName);
            Assert.Equal("node-a", succeeded.KubernetesNodeName);
        }

        private static AiRuntimeHostCreationManager CreateManager(
            IAiRuntimeHostCreationStrategy strategy,
            IAiRuntimeLifecycleJournal journal)
        {
            return new AiRuntimeHostCreationManager(
                new[] { strategy },
                NullLogger<AiRuntimeHostCreationManager>.Instance,
                new NoopAiControlPlaneObserver(),
                journal);
        }

        private static AiRuntimeHostStartRequest CreateRequest(
            AiRuntimeHostCreationMode hostCreationMode)
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId = $"host-request-{hostCreationMode}",
                ControlPlaneId = "control-plane-a",
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a"),
                HostCreationMode = hostCreationMode,
                RuntimeInstanceId = $"runtime-{hostCreationMode}",
                ProviderName = "grpc",
                TransportName = "grpc",
                TransportEndpoint = "http://127.0.0.1:5001"
            };
        }

        private sealed class RecordingRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
        {
            public RecordingRuntimeHostCreationStrategy(
                AiRuntimeHostCreationMode mode)
            {
                Mode = mode;
            }

            public AiRuntimeHostCreationMode Mode { get; }

            public AiRuntimeHostStartRequest? LastRequest { get; private set; }

            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;

                var metadata = new Dictionary<string, string>
                {
                    [AiRuntimeHostMetadataKeys.HostCreationMode] = Mode.ToString(),
                    [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(RecordingRuntimeHostCreationStrategy)
                };

                if (Mode is AiRuntimeHostCreationMode.Kubernetes or
                    AiRuntimeHostCreationMode.KubernetesPool)
                {
                    metadata[AiRuntimeHostMetadataKeys.HostId] = "pod-uid-a";
                    metadata[AiKubernetesRuntimeHostMetadataKeys.Namespace] = "runtime-tests";
                    metadata[AiKubernetesRuntimeHostMetadataKeys.PodName] = "runtime-pod-a";
                    metadata[AiKubernetesRuntimeHostMetadataKeys.NodeName] = "node-a";
                }

                return Task.FromResult(
                    AiRuntimeHostStartResult.Started(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        metadata));
            }
        }
    }
}
