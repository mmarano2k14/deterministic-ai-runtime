using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Control;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Validates lifecycle events emitted by the common runtime instance control plane.
    /// </summary>
    public sealed class AiRuntimeInstanceLifecycleJournalTests
    {
        /// <summary>
        /// Verifies that registration emits typed registered and ready events for one Kubernetes pool child.
        /// </summary>
        [Fact]
        public async Task RegisterAsync_Should_Append_Registered_And_Ready_With_Typed_Identity()
        {
            var registeredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            var registry = new RecordingRuntimeInstanceRegistry(
                AiRuntimeInstanceStatus.Ready,
                registeredAtUtc);
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var controlPlane = CreateControlPlane(registry, journal);

            var result = await controlPlane.RegisterAsync(
                new AiRuntimeInstanceControlPlaneRequest
                {
                    Operation = AiRuntimeInstanceControlPlaneOperation.Register,
                    CorrelationId = "host-request-a",
                    Registration = new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = "runtime-a1",
                        RuntimeId = "a1",
                        ControlPlaneId = "control-plane-a",
                        PoolId = "pool-a",
                        HostId = "pod-uid-a",
                        TenantId = "tenant-a",
                        TenantGroupId = "tenant-group-a",
                        KubernetesNamespace = "runtime-tests",
                        KubernetesPodName = "runtime-pool-a",
                        KubernetesNodeName = "node-a",
                        WorkerCount = 2,
                        RegisteredAtUtc = registeredAtUtc,
                        Metadata = new Dictionary<string, string>
                        {
                            [AiRuntimeHostMetadataKeys.LifecycleCorrelationId] = "host-request-a",
                            [AiRuntimeHostMetadataKeys.HostCreationMode] =
                                AiRuntimeHostCreationMode.KubernetesPool.ToString()
                        }
                    }
                });

            Assert.True(result.Success);

            var events = await journal.ListByRuntimeInstanceIdAsync("runtime-a1");

            Assert.Equal(2, events.Count);
            Assert.Equal(AiRuntimeLifecycleEvents.RuntimeRegistered, events[0].EventType);
            Assert.Equal(AiRuntimeLifecycleEvents.RuntimeReady, events[1].EventType);
            Assert.All(events, lifecycleEvent =>
            {
                Assert.Equal("control-plane-a", lifecycleEvent.ControlPlaneId);
                Assert.Equal("host-request-a", lifecycleEvent.CorrelationId);
                Assert.Equal("pool-a", lifecycleEvent.PoolId);
                Assert.Equal("pod-uid-a", lifecycleEvent.HostId);
                Assert.Equal("pod-uid-a", lifecycleEvent.KubernetesPodUid);
                Assert.Equal("runtime-tests", lifecycleEvent.KubernetesNamespace);
                Assert.Equal("runtime-pool-a", lifecycleEvent.KubernetesPodName);
                Assert.Equal("node-a", lifecycleEvent.KubernetesNodeName);
                Assert.Equal("runtime-a1", lifecycleEvent.RuntimeInstanceId);
                Assert.Equal("a1", lifecycleEvent.RuntimeId);
                Assert.Null(lifecycleEvent.TenantId);
                Assert.Null(lifecycleEvent.TenantGroupId);
            });
            Assert.Equal(events[0].EventId, events[1].CausationId);
        }

        /// <summary>
        /// Verifies that repeated ready heartbeats do not create duplicate ready events.
        /// </summary>
        [Fact]
        public async Task HeartbeatAsync_Should_Append_RuntimeReady_Only_Once_Per_Registration()
        {
            var registeredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
            var registry = new RecordingRuntimeInstanceRegistry(
                AiRuntimeInstanceStatus.Unknown,
                registeredAtUtc);
            var journal = new InMemoryAiRuntimeLifecycleJournal();
            var controlPlane = CreateControlPlane(registry, journal);

            await controlPlane.RegisterAsync(
                new AiRuntimeInstanceControlPlaneRequest
                {
                    Operation = AiRuntimeInstanceControlPlaneOperation.Register,
                    CorrelationId = "host-request-b",
                    Registration = new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = "runtime-b1",
                        ControlPlaneId = "control-plane-b",
                        HostId = "process-host-b",
                        ProcessId = 4123,
                        WorkerCount = 1,
                        RegisteredAtUtc = registeredAtUtc,
                        Metadata = new Dictionary<string, string>
                        {
                            [AiRuntimeHostMetadataKeys.LifecycleCorrelationId] = "host-request-b",
                            [AiRuntimeHostMetadataKeys.HostCreationMode] =
                                AiRuntimeHostCreationMode.Process.ToString()
                        }
                    }
                });

            var heartbeat = new AiRuntimeInstanceControlPlaneRequest
            {
                Operation = AiRuntimeInstanceControlPlaneOperation.Heartbeat,
                RuntimeInstanceId = "runtime-b1",
                CorrelationId = "host-request-b",
                Status = AiRuntimeInstanceStatus.Ready,
                CanAcceptRun = true,
                AvailableRunSlots = 1
            };

            await controlPlane.HeartbeatAsync(heartbeat);
            await controlPlane.HeartbeatAsync(heartbeat);

            var events = await journal.ListByRuntimeInstanceIdAsync("runtime-b1");

            Assert.Single(
                events.Where(lifecycleEvent =>
                    lifecycleEvent.EventType == AiRuntimeLifecycleEvents.RuntimeRegistered));
            Assert.Single(
                events.Where(lifecycleEvent =>
                    lifecycleEvent.EventType == AiRuntimeLifecycleEvents.RuntimeReady));
        }

        private static AiRuntimeInstanceControlPlane CreateControlPlane(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeLifecycleJournal lifecycleJournal)
        {
            return new AiRuntimeInstanceControlPlane(
                registry,
                Options.Create(new AiRuntimeInstanceControlPlaneOptions()),
                new NoopAiControlPlaneObserver(),
                lifecycleJournal);
        }

        private sealed class RecordingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly AiRuntimeInstanceStatus registrationStatus;
            private readonly DateTimeOffset registeredAtUtc;
            private AiRuntimeInstanceSnapshot? snapshot;

            public RecordingRuntimeInstanceRegistry(
                AiRuntimeInstanceStatus registrationStatus,
                DateTimeOffset registeredAtUtc)
            {
                this.registrationStatus = registrationStatus;
                this.registeredAtUtc = registeredAtUtc;
            }

            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                snapshot = new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = registration.RuntimeInstanceId,
                    TenantId = registration.TenantId,
                    TenantGroupId = registration.TenantGroupId,
                    Status = registrationStatus,
                    HostName = registration.HostName,
                    ProcessId = registration.ProcessId,
                    KubernetesNamespace = registration.KubernetesNamespace,
                    KubernetesPodName = registration.KubernetesPodName,
                    KubernetesNodeName = registration.KubernetesNodeName,
                    WorkerCount = registration.WorkerCount,
                    QueueCapacity = registration.QueueCapacity,
                    MaxConcurrentRuns = registration.MaxConcurrentRuns,
                    AvailableRunSlots = registration.MaxConcurrentRuns,
                    CanAcceptRun = registrationStatus == AiRuntimeInstanceStatus.Ready,
                    RegisteredAtUtc = registration.RegisteredAtUtc == default
                        ? registeredAtUtc
                        : registration.RegisteredAtUtc,
                    LastHeartbeatAtUtc = registration.RegisteredAtUtc == default
                        ? registeredAtUtc
                        : registration.RegisteredAtUtc,
                    RuntimeVersion = registration.RuntimeVersion,
                    Metadata = registration.Metadata,
                    Role = registration.Role,
                    HostId = registration.HostId,
                    PoolId = registration.PoolId,
                    RuntimeId = registration.RuntimeId,
                    ControlPlaneHostId = registration.ControlPlaneHostId,
                    ControlPlaneId = registration.ControlPlaneId
                };

                return Task.FromResult(snapshot);
            }

            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                if (snapshot is null)
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                snapshot = CopySnapshot(
                    snapshot,
                    status,
                    queuedRunCount,
                    runningRunCount,
                    activeRunCount,
                    availableRunSlots,
                    activeWorkerCount,
                    availableWorkerCount,
                    maxLocalWorkersPerExecution,
                    isQueuePaused,
                    canAcceptRun);

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(snapshot);
            }

            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(snapshot);
            }

            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceSnapshot> result = snapshot is null
                    ? Array.Empty<AiRuntimeInstanceSnapshot>()
                    : new[] { snapshot };

                return Task.FromResult(result);
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return SetStatusAsync(AiRuntimeInstanceStatus.Draining);
            }

            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return SetStatusAsync(AiRuntimeInstanceStatus.Unhealthy);
            }

            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return SetStatusAsync(AiRuntimeInstanceStatus.Stopped);
            }

            private Task<AiRuntimeInstanceSnapshot?> SetStatusAsync(
                AiRuntimeInstanceStatus status)
            {
                if (snapshot is null)
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                snapshot = CopySnapshot(
                    snapshot,
                    status,
                    snapshot.QueuedRunCount,
                    snapshot.RunningRunCount,
                    snapshot.ActiveRunCount,
                    snapshot.AvailableRunSlots,
                    snapshot.ActiveWorkerCount,
                    snapshot.AvailableWorkerCount,
                    snapshot.MaxLocalWorkersPerExecution,
                    snapshot.IsQueuePaused,
                    snapshot.CanAcceptRun);

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(snapshot);
            }

            private static AiRuntimeInstanceSnapshot CopySnapshot(
                AiRuntimeInstanceSnapshot source,
                AiRuntimeInstanceStatus status,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = source.RuntimeInstanceId,
                    TenantId = source.TenantId,
                    TenantGroupId = source.TenantGroupId,
                    Status = status,
                    HostName = source.HostName,
                    ProcessId = source.ProcessId,
                    KubernetesNamespace = source.KubernetesNamespace,
                    KubernetesPodName = source.KubernetesPodName,
                    KubernetesNodeName = source.KubernetesNodeName,
                    WorkerCount = source.WorkerCount,
                    QueuedRunCount = queuedRunCount,
                    RunningRunCount = runningRunCount,
                    ActiveRunCount = activeRunCount,
                    QueueCapacity = source.QueueCapacity,
                    MaxConcurrentRuns = source.MaxConcurrentRuns,
                    AvailableRunSlots = availableRunSlots,
                    IsQueuePaused = isQueuePaused,
                    CanAcceptRun = canAcceptRun,
                    RegisteredAtUtc = source.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    RuntimeVersion = source.RuntimeVersion,
                    Metadata = source.Metadata,
                    Role = source.Role,
                    ActiveWorkerCount = activeWorkerCount,
                    AvailableWorkerCount = availableWorkerCount,
                    MaxLocalWorkersPerExecution = maxLocalWorkersPerExecution,
                    HostId = source.HostId,
                    PoolId = source.PoolId,
                    RuntimeId = source.RuntimeId,
                    ControlPlaneHostId = source.ControlPlaneHostId,
                    ControlPlaneId = source.ControlPlaneId
                };
            }
        }
    }
}
