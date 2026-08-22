using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    public sealed class ProductionRuntimeTopologySummaryOutputTests
    {
        [Fact]
        public void Build_Should_Group_KubernetesPool_Runtimes_By_Pod_And_Map_Run_Movement()
        {
            var snapshots = new[]
            {
                CreateSnapshot(
                    runtimeInstanceId: "pool-runtime-1",
                    controlPlaneId: "control-plane-a",
                    poolId: "pool-a",
                    hostId: "pod-uid-a",
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Unhealthy,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "runtime-pool-a",
                    kubernetesNodeName: "node-a"),
                CreateSnapshot(
                    runtimeInstanceId: "pool-runtime-2",
                    controlPlaneId: "control-plane-a",
                    poolId: "pool-a",
                    hostId: "pod-uid-a",
                    runtimeId: "runtime-2",
                    status: AiRuntimeInstanceStatus.Stopped,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "runtime-pool-a",
                    kubernetesNodeName: "node-a"),
                CreateSnapshot(
                    runtimeInstanceId: "pool-runtime-replacement-1",
                    controlPlaneId: "control-plane-a",
                    poolId: "pool-a",
                    hostId: "pod-uid-b",
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Ready,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "runtime-pool-b",
                    kubernetesNodeName: "node-b")
            };

            var placements = new[]
            {
                new ProductionRuntimeRunPlacement
                {
                    TenantId = "tenant-a",
                    TenantRole = "Impacted",
                    SharedRunId = "shared-run-1",
                    WorkKind = "InFlightExecution",
                    PipelineName = "pipeline-a",
                    InitialRuntimeInstanceId = "pool-runtime-1",
                    InitialLocalRunId = "local-run-1",
                    InitialExecutionId = "execution-1",
                    CurrentRuntimeInstanceId = "pool-runtime-replacement-1",
                    CurrentLocalRunId = "local-run-2",
                    CurrentExecutionId = "execution-1"
                }
            };

            var summary =
                ProductionRuntimeTopologySummaryOutput.Build(
                    "control-plane-a",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    snapshots,
                    placements);

            Assert.Contains("ObservedPhysicalHostCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("ObservedKubernetesPodCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("Kind='KubernetesPod', DisplayName='multiplexed-ai/runtime-pool-a'", summary, StringComparison.Ordinal);
            Assert.Contains("RuntimeCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialHost='multiplexed-ai/runtime-pool-a'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentHost='multiplexed-ai/runtime-pool-b'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentExecutionId='execution-1', Moved='true'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_Should_Group_ProcessPool_Children_By_Generic_HostId()
        {
            var snapshots = new[]
            {
                CreateSnapshot(
                    runtimeInstanceId: "process-pool-runtime-1",
                    controlPlaneId: "control-plane-process-pool",
                    poolId: "process-pool-a",
                    hostId: "process-pool-host-a",
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Ready,
                    processId: 1001),
                CreateSnapshot(
                    runtimeInstanceId: "process-pool-runtime-2",
                    controlPlaneId: "control-plane-process-pool",
                    poolId: "process-pool-a",
                    hostId: "process-pool-host-a",
                    runtimeId: "runtime-2",
                    status: AiRuntimeInstanceStatus.Busy,
                    processId: 1002)
            };

            var placements = new[]
            {
                new ProductionRuntimeRunPlacement
                {
                    TenantId = "tenant-process",
                    TenantRole = "Safe",
                    SharedRunId = "shared-run-process",
                    WorkKind = "LocalQueued",
                    InitialRuntimeInstanceId = "process-pool-runtime-1",
                    InitialLocalRunId = "local-process-1",
                    CurrentRuntimeInstanceId = "process-pool-runtime-2",
                    CurrentLocalRunId = "local-process-2",
                    CurrentExecutionId = "execution-process"
                }
            };

            var summary =
                ProductionRuntimeTopologySummaryOutput.Build(
                    "control-plane-process-pool",
                    AiRuntimeHostCreationMode.Process,
                    snapshots,
                    placements);

            Assert.Contains("ObservedPhysicalHostCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("ObservedKubernetesPodCount='0'", summary, StringComparison.Ordinal);
            Assert.Contains("Kind='RuntimePoolHost', DisplayName='process-pool-host-a'", summary, StringComparison.Ordinal);
            Assert.Contains("RuntimeCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("TenantIds='tenant-process'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_Should_Project_Standalone_Process_Mode_And_Exclude_Foreign_ControlPlane()
        {
            var snapshots = new[]
            {
                CreateSnapshot(
                    runtimeInstanceId: "process-runtime-1",
                    controlPlaneId: "control-plane-process",
                    poolId: null,
                    hostId: null,
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Ready,
                    hostName: "machine-a",
                    processId: 2001),
                CreateSnapshot(
                    runtimeInstanceId: "foreign-runtime",
                    controlPlaneId: "foreign-control-plane",
                    poolId: null,
                    hostId: null,
                    runtimeId: "runtime-foreign",
                    status: AiRuntimeInstanceStatus.Ready,
                    hostName: "machine-a",
                    processId: 9999)
            };

            var placements = new[]
            {
                new ProductionRuntimeRunPlacement
                {
                    TenantId = "tenant-process",
                    TenantRole = "Safe",
                    SharedRunId = "shared-run-process",
                    WorkKind = "InFlightExecution",
                    InitialRuntimeInstanceId = "process-runtime-1",
                    InitialLocalRunId = "local-process",
                    InitialExecutionId = "execution-process",
                    CurrentRuntimeInstanceId = "process-runtime-1",
                    CurrentLocalRunId = "local-process",
                    CurrentExecutionId = "execution-process"
                }
            };

            var summary =
                ProductionRuntimeTopologySummaryOutput.Build(
                    "control-plane-process",
                    AiRuntimeHostCreationMode.Process,
                    snapshots,
                    placements);

            Assert.Contains("Kind='Process', DisplayName='machine-a:2001'", summary, StringComparison.Ordinal);
            Assert.Contains("ObservedRuntimeInstanceCount='1'", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-runtime", summary, StringComparison.Ordinal);
            Assert.Contains("Moved='false'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_Should_Retain_Deleted_Initial_Pod_From_Historical_Snapshots()
        {
            var historicalSnapshots = new[]
            {
                CreateSnapshot(
                    runtimeInstanceId: "initial-runtime-1",
                    controlPlaneId: "control-plane-history",
                    poolId: "pool-history",
                    hostId: "deleted-pod-uid",
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Ready,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "deleted-pod",
                    kubernetesNodeName: "node-a"),
                CreateSnapshot(
                    runtimeInstanceId: "initial-runtime-2",
                    controlPlaneId: "control-plane-history",
                    poolId: "pool-history",
                    hostId: "deleted-pod-uid",
                    runtimeId: "runtime-2",
                    status: AiRuntimeInstanceStatus.Busy,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "deleted-pod",
                    kubernetesNodeName: "node-a"),
                CreateSnapshot(
                    runtimeInstanceId: "initial-runtime-3",
                    controlPlaneId: "control-plane-history",
                    poolId: "pool-history",
                    hostId: "deleted-pod-uid",
                    runtimeId: "runtime-3",
                    status: AiRuntimeInstanceStatus.Ready,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "deleted-pod",
                    kubernetesNodeName: "node-a")
            };

            var currentSnapshots = new[]
            {
                CreateSnapshot(
                    runtimeInstanceId: "replacement-runtime-1",
                    controlPlaneId: "control-plane-history",
                    poolId: "pool-history",
                    hostId: "replacement-pod-uid",
                    runtimeId: "runtime-1",
                    status: AiRuntimeInstanceStatus.Ready,
                    kubernetesNamespace: "multiplexed-ai",
                    kubernetesPodName: "replacement-pod",
                    kubernetesNodeName: "node-a")
            };

            var placements = new[]
            {
                new ProductionRuntimeRunPlacement
                {
                    TenantId = "tenant-history",
                    TenantRole = "Impacted",
                    SharedRunId = "shared-run-history",
                    WorkKind = "InFlightExecution",
                    InitialRuntimeInstanceId = "initial-runtime-2",
                    InitialLocalRunId = "initial-local-run",
                    InitialExecutionId = "execution-history",
                    CurrentRuntimeInstanceId = "replacement-runtime-1",
                    CurrentLocalRunId = "replacement-local-run",
                    CurrentExecutionId = "execution-history"
                }
            };

            var summary =
                ProductionRuntimeTopologySummaryOutput.Build(
                    "control-plane-history",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    currentSnapshots,
                    placements,
                    historicalSnapshots);

            Assert.Contains("ObservedKubernetesPodCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("ActiveKubernetesPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalOnlyKubernetesPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("PodName='deleted-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("Lifecycle='HistoricalOnly'", summary, StringComparison.Ordinal);
            Assert.Contains("RuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentRegistryRuntimeCount='0', HistoricalOnlyRuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("SnapshotSource='HistoricalBeforeFailure'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialHost='multiplexed-ai/deleted-pod'", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("InitialHostKind='Unknown'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildFromLifecycleEvents_Should_Reconstruct_Deleted_Pod_Replacement_And_Run_Correlation()
        {
            var summary =
                ProductionRuntimeTopologySummaryOutput.BuildFromLifecycleEvents(
                    "control-plane-durable",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    CreateDurableLifecycleEvents(),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tenant-impacted"] = "Impacted",
                        ["tenant-safe"] = "Safe"
                    });

            Assert.Contains("TopologySource='RuntimeLifecycleJournal'", summary, StringComparison.Ordinal);
            Assert.Contains("ObservedKubernetesPodCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalOnlyKubernetesPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("DeletedPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalRuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("RunPlacementCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("PodName='initial-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("Lifecycle='HistoricalOnly'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalOnlyRuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("SnapshotSource='DurableHistory'", summary, StringComparison.Ordinal);
            Assert.Contains("SnapshotSource='DurableCurrent'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialPodUid='initial-pod-uid'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialPodName='initial-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentPodUid='replacement-pod-uid'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentPodName='replacement-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentExecutionId='execution-1', Moved='true'", summary, StringComparison.Ordinal);
            Assert.Contains("SharedRunId='shared-local-queued'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialRuntimeInstanceId='initial-runtime-2'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialLocalRunId='local-queued-initial'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentRuntimeInstanceId='replacement-runtime-2'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentLocalRunId='local-queued-current'", summary, StringComparison.Ordinal);
            Assert.Contains("WorkKind='LocalQueued'", summary, StringComparison.Ordinal);
            Assert.Contains("Moved='true'", summary, StringComparison.Ordinal);
            Assert.Contains("RuntimeFailureIncidentId='failure-1'", summary, StringComparison.Ordinal);
            Assert.Contains("LedgerEntryId='ledger-1'", summary, StringComparison.Ordinal);
            Assert.Contains("ForensicsId='forensics-1'", summary, StringComparison.Ordinal);
            Assert.Contains("SharedRunId='shared-safe'", summary, StringComparison.Ordinal);
            Assert.Contains("RuntimeInstanceId='safe-runtime'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialHost='multiplexed-ai/safe-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentHost='multiplexed-ai/safe-pod'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentExecutionId='execution-safe', Moved='false'", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-runtime", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("transient-runtime", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("transient-pod", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("InitialHostKind='Unknown'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildFromLifecycleEvents_Should_Not_Count_Replacement_Pod_As_Deleted_When_Recovered_Work_Is_Released()
        {
            var start = DateTimeOffset.Parse("2026-08-10T00:00:00Z");
            var events = new List<AiRuntimeLifecycleEvent>();

            AddRuntime(
                events,
                start,
                "control-plane-replacement-release",
                "failed-runtime",
                "runtime-1",
                "pool-a",
                "failed-pod-uid",
                "runtime-pool-pod",
                0);

            events.Add(CreateLifecycleEvent(
                "work-assigned",
                AiRuntimeLifecycleEvents.WorkAssigned,
                start.AddSeconds(10),
                "control-plane-replacement-release",
                runtimeInstanceId: "failed-runtime",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "failed-pod-uid",
                kubernetesPodUid: "failed-pod-uid",
                kubernetesPodName: "runtime-pool-pod",
                tenantId: "tenant-a",
                sharedRunId: "shared-1",
                localRunId: "local-failed",
                executionId: "execution-1"));

            events.Add(CreateLifecycleEvent(
                "host-disappeared",
                AiRuntimeLifecycleEvents.HostDisappeared,
                start.AddSeconds(20),
                "control-plane-replacement-release",
                poolId: "pool-a",
                hostId: "failed-pod-uid",
                kubernetesPodUid: "failed-pod-uid",
                kubernetesPodName: "runtime-pool-pod",
                runtimeFailureIncidentId: "failure-1"));

            events.Add(CreateLifecycleEvent(
                "runtime-suppressed",
                AiRuntimeLifecycleEvents.RuntimeSuppressed,
                start.AddSeconds(21),
                "control-plane-replacement-release",
                runtimeInstanceId: "failed-runtime",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "failed-pod-uid",
                kubernetesPodUid: "failed-pod-uid",
                kubernetesPodName: "runtime-pool-pod",
                runtimeFailureIncidentId: "failure-1"));

            AddRuntime(
                events,
                start,
                "control-plane-replacement-release",
                "replacement-runtime",
                "runtime-1",
                "pool-a",
                "replacement-pod-uid",
                "runtime-pool-pod",
                30,
                replacement: true);

            events.Add(CreateLifecycleEvent(
                "work-reassigned",
                AiRuntimeLifecycleEvents.WorkReassigned,
                start.AddSeconds(31),
                "control-plane-replacement-release",
                runtimeInstanceId: "replacement-runtime",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "replacement-pod-uid",
                kubernetesPodUid: "replacement-pod-uid",
                kubernetesPodName: "runtime-pool-pod",
                tenantId: "tenant-a",
                sharedRunId: "shared-1",
                localRunId: "local-replacement",
                executionId: "execution-1",
                runtimeFailureIncidentId: "failure-1"));

            events.Add(CreateLifecycleEvent(
                "work-released-after-recovery",
                AiRuntimeLifecycleEvents.WorkReleased,
                start.AddSeconds(40),
                "control-plane-replacement-release",
                runtimeInstanceId: "replacement-runtime",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "replacement-pod-uid",
                kubernetesPodUid: "replacement-pod-uid",
                kubernetesPodName: "runtime-pool-pod",
                tenantId: "tenant-a",
                sharedRunId: "shared-1",
                localRunId: "local-replacement",
                executionId: "execution-1",
                runtimeFailureIncidentId: "failure-1"));

            var summary =
                ProductionRuntimeTopologySummaryOutput.BuildFromLifecycleEvents(
                    "control-plane-replacement-release",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    events,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["tenant-a"] = "Impacted"
                    });

            Assert.Contains("DeletedPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalOnlyKubernetesPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("InitialPodUid='failed-pod-uid'", summary, StringComparison.Ordinal);
            Assert.Contains("CurrentPodUid='replacement-pod-uid'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildFromLifecycleEvents_Should_Count_IncidentFacts_When_RuntimeIdentity_Is_Reused()
        {
            var events = CreateDurableLifecycleEvents().ToList();
            var start = DateTimeOffset.Parse("2026-07-30T00:00:00Z");

            for (var index = 1; index <= 3; index++)
            {
                AddRuntime(
                    events,
                    start,
                    "control-plane-durable",
                    $"initial-runtime-{index}",
                    $"runtime-{index}",
                    "pool-a",
                    "reused-replacement-pod-uid",
                    "reused-replacement-pod",
                    60 + (index * 2),
                    replacement: true);
            }

            var summary =
                ProductionRuntimeTopologySummaryOutput.BuildFromLifecycleEvents(
                    "control-plane-durable",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    events);

            Assert.Contains("HistoricalOnlyKubernetesPodCount='0'", summary, StringComparison.Ordinal);
            Assert.Contains("DeletedPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalRuntimeCount='3'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateFromLifecycleJournalAsync_Should_Query_Durable_Journal_As_Primary_Source()
        {
            var journal = new StubLifecycleJournal(CreateDurableLifecycleEvents());

            var summary =
                await ProductionRuntimeTopologySummaryOutput.CreateFromLifecycleJournalAsync(
                    journal,
                    "control-plane-durable",
                    AiRuntimeHostCreationMode.KubernetesPool);

            Assert.Equal(1, journal.ListByControlPlaneCallCount);
            Assert.Contains("TopologySource='RuntimeLifecycleJournal'", summary, StringComparison.Ordinal);
            Assert.Contains("ControlPlaneId='control-plane-durable'", summary, StringComparison.Ordinal);
            Assert.Contains("PodName='initial-pod'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateFromLifecycleJournalAsync_Should_Expand_The_ControlPlane_History_By_Failure_Incident()
        {
            var start = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
            var incidentId = "failure-cross-control-plane";
            var events = new List<AiRuntimeLifecycleEvent>
            {
                new()
                {
                    EventId = "work-released-1",
                    EventType = AiRuntimeLifecycleEvents.WorkReleased,
                    TimestampUtc = start,
                    ControlPlaneId = "control-plane-incident",
                    HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                    PoolId = "pool-incident",
                    HostId = "failed-pod-uid",
                    RuntimeInstanceId = "failed-runtime-1",
                    TenantId = "tenant-impacted",
                    SharedRunId = "shared-run-1",
                    LocalRunId = "failed-local-run-1",
                    RuntimeFailureIncidentId = incidentId
                }
            };

            for (var index = 1; index <= 3; index++)
            {
                events.Add(
                    new AiRuntimeLifecycleEvent
                    {
                        EventId = $"runtime-unhealthy-{index}",
                        EventType = AiRuntimeLifecycleEvents.RuntimeUnhealthy,
                        TimestampUtc = start.AddTicks(index),
                        ControlPlaneId = "runtime-lifecycle",
                        HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                        PoolId = "pool-incident",
                        HostId = "failed-pod-uid",
                        KubernetesPodUid = "failed-pod-uid",
                        KubernetesNamespace = "multiplexed-ai",
                        KubernetesPodName = "failed-pod",
                        RuntimeInstanceId = $"failed-runtime-{index}",
                        RuntimeFailureIncidentId = incidentId,
                        PreviousStatus = "selectable",
                        CurrentStatus = "unhealthy"
                    });
            }

            var journal = new StubLifecycleJournal(events);

            var summary =
                await ProductionRuntimeTopologySummaryOutput.CreateFromLifecycleJournalAsync(
                    journal,
                    "control-plane-incident",
                    AiRuntimeHostCreationMode.KubernetesPool);

            Assert.Equal(1, journal.ListByControlPlaneCallCount);
            Assert.Equal(1, journal.ListByIncidentCallCount);
            Assert.Contains("TopologySource='RuntimeLifecycleJournal'", summary, StringComparison.Ordinal);
            Assert.Contains("DeletedPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalRuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("PodUid='failed-pod-uid'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CreateFromLifecycleJournalWithPlacementSeedsAsync_Should_Discover_Incident_From_Moved_Initial_Runtime()
        {
            var start = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
            var incidentId = "failure-discovered-from-runtime";
            var failedRuntimeId = "failed-runtime-1";
            var events = new List<AiRuntimeLifecycleEvent>
            {
                new()
                {
                    EventId = "initial-work-assigned",
                    EventType = AiRuntimeLifecycleEvents.WorkAssigned,
                    TimestampUtc = start,
                    ControlPlaneId = "control-plane-runtime-seed",
                    HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                    RuntimeInstanceId = failedRuntimeId,
                    TenantId = "tenant-impacted",
                    SharedRunId = "shared-run-1",
                    LocalRunId = "failed-local-run-1"
                }
            };

            for (var index = 1; index <= 3; index++)
            {
                events.Add(
                    new AiRuntimeLifecycleEvent
                    {
                        EventId = $"runtime-unhealthy-seeded-{index}",
                        EventType = AiRuntimeLifecycleEvents.RuntimeUnhealthy,
                        TimestampUtc = start.AddTicks(index),
                        ControlPlaneId = "runtime-lifecycle",
                        HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                        PoolId = "pool-runtime-seed",
                        HostId = "failed-pod-uid",
                        KubernetesPodUid = "failed-pod-uid",
                        KubernetesNamespace = "multiplexed-ai",
                        KubernetesPodName = "failed-pod",
                        RuntimeInstanceId = $"failed-runtime-{index}",
                        RuntimeFailureIncidentId = incidentId,
                        PreviousStatus = "selectable",
                        CurrentStatus = "unhealthy"
                    });
            }

            var placements = new[]
            {
                new ProductionRuntimeRunPlacement
                {
                    TenantId = "tenant-impacted",
                    TenantRole = "Impacted",
                    SharedRunId = "shared-run-1",
                    WorkKind = "InFlightExecution",
                    InitialRuntimeInstanceId = failedRuntimeId,
                    InitialLocalRunId = "failed-local-run-1",
                    InitialExecutionId = "execution-1",
                    CurrentRuntimeInstanceId = "replacement-runtime-1",
                    CurrentLocalRunId = "replacement-local-run-1",
                    CurrentExecutionId = "execution-1"
                }
            };
            var journal = new StubLifecycleJournal(events);

            var summary =
                await ProductionRuntimeTopologySummaryOutput
                    .CreateFromLifecycleJournalWithPlacementSeedsAsync(
                        journal,
                        "control-plane-runtime-seed",
                        AiRuntimeHostCreationMode.KubernetesPool,
                        placements);

            Assert.Equal(1, journal.ListByControlPlaneCallCount);
            Assert.Equal(1, journal.ListByRuntimeCallCount);
            Assert.Equal(1, journal.ListByIncidentCallCount);
            Assert.Contains("TopologySource='RuntimeLifecycleJournal'", summary, StringComparison.Ordinal);
            Assert.Contains("DeletedPodCount='1'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalRuntimeCount='3'", summary, StringComparison.Ordinal);
            Assert.Contains("PodUid='failed-pod-uid'", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void Build_Should_Use_RuntimeIdentity_Fallback_When_Placement_Snapshot_Is_Missing()
        {
            var placement = new ProductionRuntimeRunPlacement
            {
                TenantId = "tenant-safe",
                TenantRole = "Safe",
                SharedRunId = "shared-run-safe",
                WorkKind = "LocalQueued",
                InitialRuntimeInstanceId = "runtime-placement-only",
                InitialLocalRunId = "local-run-safe",
                CurrentRuntimeInstanceId = "runtime-placement-only",
                CurrentLocalRunId = "local-run-safe",
                CurrentExecutionId = "execution-safe"
            };

            var scenarioSummary =
                ProductionRuntimeTopologySummaryOutput.Build(
                    "control-plane-placement-only",
                    AiRuntimeHostCreationMode.KubernetesPool,
                    Array.Empty<AiRuntimeInstanceSnapshot>(),
                    new[] { placement },
                    topologySource: "RuntimeLifecycleJournal");

            Assert.Contains(
                "InitialHostKind='RuntimeHost', InitialHost='runtime-placement-only'",
                scenarioSummary,
                StringComparison.Ordinal);
            Assert.Contains(
                "CurrentHostKind='RuntimeHost', CurrentHost='runtime-placement-only'",
                scenarioSummary,
                StringComparison.Ordinal);
            Assert.Contains("Moved='false'", scenarioSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("InitialHostKind='Unknown'", scenarioSummary, StringComparison.Ordinal);

            var parallelSummary =
                ProductionRuntimeTopologySummaryOutput.BuildParallel(
                    new[] { scenarioSummary },
                    expectedScenarioCount: 1);

            Assert.Contains("UnknownInitialHostCount='0'", parallelSummary, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildParallel_Should_Write_One_Final_Grouped_Section()
        {
            var summary =
                ProductionRuntimeTopologySummaryOutput.BuildParallel(
                    new[]
                    {
                        "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY\nControlPlaneId='control-plane-b'\nObservedKubernetesPodCount='3'\nHistoricalOnlyKubernetesPodCount='0'\nDeletedPodCount='1'\nHistoricalRuntimeCount='3'\nRunPlacementCount='2'\nHost[01] HistoricalOnlyRuntimeCount='0'\nRun[01] Moved='true'\nRun[02] Moved='false'",
                        "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY\nControlPlaneId='control-plane-a'\nObservedKubernetesPodCount='2'\nHistoricalOnlyKubernetesPodCount='1'\nRunPlacementCount='2'\nHost[01] HistoricalOnlyRuntimeCount='3'\nRun[01] Moved='true'\nRun[02] Moved='false'"
                    },
                    expectedScenarioCount: 2);

            Assert.Equal(
                1,
                summary.Split(
                    "# PARALLEL RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY",
                    StringSplitOptions.None).Length - 1);
            Assert.Contains("CapturedScenarioCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("MissingScenarioCount='0'", summary, StringComparison.Ordinal);
            Assert.Contains("ObservedPodCount='5'", summary, StringComparison.Ordinal);
            Assert.Contains("DeletedPodCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("HistoricalRuntimeCount='6'", summary, StringComparison.Ordinal);
            Assert.Contains("RunPlacementCount='4'", summary, StringComparison.Ordinal);
            Assert.Contains("MovedRunCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("StableRunCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("UnknownInitialHostCount='0'", summary, StringComparison.Ordinal);
            Assert.True(
                summary.IndexOf("control-plane-a", StringComparison.Ordinal) <
                summary.IndexOf("control-plane-b", StringComparison.Ordinal));
            Assert.Equal(
                2,
                summary.Split(
                    "## SCENARIO RUNTIME TOPOLOGY AND RUN PLACEMENT",
                    StringSplitOptions.None).Length - 1);
        }

        private static IReadOnlyCollection<AiRuntimeLifecycleEvent> CreateDurableLifecycleEvents()
        {
            var start = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
            var events = new List<AiRuntimeLifecycleEvent>();

            AddRuntime(events, start, "control-plane-durable", "initial-runtime-1", "runtime-1", "pool-a", "initial-pod-uid", "initial-pod", 0);
            AddRuntime(events, start, "control-plane-durable", "initial-runtime-2", "runtime-2", "pool-a", "initial-pod-uid", "initial-pod", 2);
            AddRuntime(events, start, "control-plane-durable", "initial-runtime-3", "runtime-3", "pool-a", "initial-pod-uid", "initial-pod", 4);

            events.Add(CreateLifecycleEvent(
                "work-assigned-impacted",
                AiRuntimeLifecycleEvents.WorkAssigned,
                start.AddSeconds(10),
                "control-plane-durable",
                runtimeInstanceId: "initial-runtime-1",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "initial-pod-uid",
                kubernetesPodUid: "initial-pod-uid",
                kubernetesPodName: "initial-pod",
                tenantId: "tenant-impacted",
                sharedRunId: "shared-impacted",
                localRunId: "local-initial",
                executionId: "execution-1"));
            events.Add(CreateLifecycleEvent(
                "work-assigned-safe",
                AiRuntimeLifecycleEvents.WorkAssigned,
                start.AddSeconds(11),
                "control-plane-durable",
                runtimeInstanceId: "safe-runtime",
                runtimeId: "runtime-safe",
                poolId: "pool-safe",
                hostId: "safe-pod-uid",
                kubernetesPodUid: "safe-pod-uid",
                kubernetesPodName: "safe-pod",
                tenantId: "tenant-safe",
                sharedRunId: "shared-safe",
                localRunId: "local-safe",
                executionId: "execution-safe"));
            events.Add(CreateLifecycleEvent(
                "work-released-safe-after-completion",
                AiRuntimeLifecycleEvents.WorkReleased,
                start.AddSeconds(19),
                "control-plane-durable",
                runtimeInstanceId: "safe-runtime",
                runtimeId: "runtime-safe",
                poolId: "pool-safe",
                hostId: "safe-pod-uid",
                kubernetesPodUid: "safe-pod-uid",
                kubernetesPodName: "safe-pod",
                tenantId: "tenant-safe",
                sharedRunId: "shared-safe",
                localRunId: "local-safe",
                executionId: "execution-safe"));

            events.Add(CreateLifecycleEvent(
                "host-disappeared",
                AiRuntimeLifecycleEvents.HostDisappeared,
                start.AddSeconds(20),
                "control-plane-durable",
                poolId: "pool-a",
                hostId: "initial-pod-uid",
                kubernetesPodUid: "initial-pod-uid",
                kubernetesPodName: "initial-pod",
                runtimeFailureIncidentId: "failure-1"));

            for (var index = 1; index <= 3; index++)
            {
                events.Add(CreateLifecycleEvent(
                    $"runtime-suppressed-{index}",
                    AiRuntimeLifecycleEvents.RuntimeSuppressed,
                    start.AddSeconds(20 + index),
                    "control-plane-durable",
                    runtimeInstanceId: $"initial-runtime-{index}",
                    runtimeId: $"runtime-{index}",
                    poolId: "pool-a",
                    hostId: "initial-pod-uid",
                    kubernetesPodUid: "initial-pod-uid",
                    kubernetesPodName: "initial-pod",
                    runtimeFailureIncidentId: "failure-1"));
            }

            AddRuntime(events, start, "control-plane-durable", "replacement-runtime-1", "runtime-1", "pool-a", "replacement-pod-uid", "replacement-pod", 30, replacement: true);
            AddRuntime(events, start, "control-plane-durable", "replacement-runtime-2", "runtime-2", "pool-a", "replacement-pod-uid", "replacement-pod", 32, replacement: true);
            AddRuntime(events, start, "control-plane-durable", "replacement-runtime-3", "runtime-3", "pool-a", "replacement-pod-uid", "replacement-pod", 34, replacement: true);

            events.Add(CreateLifecycleEvent(
                "work-released-impacted",
                AiRuntimeLifecycleEvents.WorkReleased,
                start.AddSeconds(40),
                "control-plane-durable",
                runtimeInstanceId: "initial-runtime-1",
                tenantId: "tenant-impacted",
                sharedRunId: "shared-impacted",
                localRunId: "local-initial",
                executionId: "execution-1",
                runtimeFailureIncidentId: "failure-1",
                ledgerEntryId: "ledger-1",
                forensicsId: "forensics-1"));
            events.Add(CreateLifecycleEvent(
                "work-reassigned-impacted",
                AiRuntimeLifecycleEvents.WorkReassigned,
                start.AddSeconds(41),
                "control-plane-durable",
                runtimeInstanceId: "replacement-runtime-1",
                runtimeId: "runtime-1",
                poolId: "pool-a",
                hostId: "replacement-pod-uid",
                kubernetesPodUid: "replacement-pod-uid",
                kubernetesPodName: "replacement-pod",
                tenantId: "tenant-impacted",
                sharedRunId: "shared-impacted",
                localRunId: "local-current",
                executionId: "execution-1",
                runtimeFailureIncidentId: "failure-1",
                ledgerEntryId: "ledger-1",
                forensicsId: "forensics-1"));
            events.Add(CreateLifecycleEvent(
                "work-released-local-queued",
                AiRuntimeLifecycleEvents.WorkReleased,
                start.AddSeconds(42),
                "control-plane-durable",
                runtimeInstanceId: "initial-runtime-2",
                runtimeId: "runtime-2",
                poolId: "pool-a",
                hostId: "initial-pod-uid",
                kubernetesPodUid: "initial-pod-uid",
                kubernetesPodName: "initial-pod",
                tenantId: "tenant-impacted",
                sharedRunId: "shared-local-queued",
                localRunId: "local-queued-initial",
                runtimeFailureIncidentId: "failure-1",
                forensicsId: "forensics-local-queued",
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["recovery.candidateKind"] = "LocalQueued"
                }));
            events.Add(CreateLifecycleEvent(
                "work-reassigned-local-queued",
                AiRuntimeLifecycleEvents.WorkReassigned,
                start.AddSeconds(43),
                "control-plane-durable",
                runtimeInstanceId: "replacement-runtime-2",
                runtimeId: "runtime-2",
                poolId: "pool-a",
                hostId: "replacement-pod-uid",
                kubernetesPodUid: "replacement-pod-uid",
                kubernetesPodName: "replacement-pod",
                tenantId: "tenant-impacted",
                sharedRunId: "shared-local-queued",
                localRunId: "local-queued-current",
                runtimeFailureIncidentId: "failure-1",
                forensicsId: "forensics-local-queued",
                metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["failed.runtimeInstanceId"] = "initial-runtime-2",
                    ["failed.localRunId"] = "local-queued-initial"
                }));

            AddRuntime(
                events,
                start,
                "control-plane-durable",
                "transient-runtime",
                "runtime-transient",
                "pool-transient",
                "transient-pod-uid",
                "transient-pod",
                44);
            events.Add(CreateLifecycleEvent(
                "transient-runtime-stopped",
                AiRuntimeLifecycleEvents.RuntimeStopped,
                start.AddSeconds(46),
                "control-plane-durable",
                runtimeInstanceId: "transient-runtime",
                runtimeId: "runtime-transient",
                poolId: "pool-transient",
                hostId: "transient-pod-uid",
                kubernetesPodUid: "transient-pod-uid",
                kubernetesPodName: "transient-pod"));
            events.Add(CreateLifecycleEvent(
                "transient-host-deleted",
                AiRuntimeLifecycleEvents.HostDeleted,
                start.AddSeconds(47),
                "control-plane-durable",
                poolId: "pool-transient",
                hostId: "transient-pod-uid",
                kubernetesPodUid: "transient-pod-uid",
                kubernetesPodName: "transient-pod"));

            events.Add(CreateLifecycleEvent(
                "foreign-runtime",
                AiRuntimeLifecycleEvents.RuntimeReady,
                start.AddSeconds(50),
                "foreign-control-plane",
                runtimeInstanceId: "foreign-runtime",
                hostId: "foreign-pod-uid",
                kubernetesPodUid: "foreign-pod-uid",
                kubernetesPodName: "foreign-pod"));

            return events;
        }

        private static void AddRuntime(
            ICollection<AiRuntimeLifecycleEvent> events,
            DateTimeOffset start,
            string controlPlaneId,
            string runtimeInstanceId,
            string runtimeId,
            string poolId,
            string podUid,
            string podName,
            int secondOffset,
            bool replacement = false)
        {
            events.Add(CreateLifecycleEvent(
                $"{runtimeInstanceId}-registered",
                replacement
                    ? AiRuntimeLifecycleEvents.RuntimeReplacementRegistered
                    : AiRuntimeLifecycleEvents.RuntimeRegistered,
                start.AddSeconds(secondOffset),
                controlPlaneId,
                runtimeInstanceId: runtimeInstanceId,
                runtimeId: runtimeId,
                poolId: poolId,
                hostId: podUid,
                kubernetesPodUid: podUid,
                kubernetesPodName: podName));
            events.Add(CreateLifecycleEvent(
                $"{runtimeInstanceId}-ready",
                AiRuntimeLifecycleEvents.RuntimeReady,
                start.AddSeconds(secondOffset + 1),
                controlPlaneId,
                runtimeInstanceId: runtimeInstanceId,
                runtimeId: runtimeId,
                poolId: poolId,
                hostId: podUid,
                kubernetesPodUid: podUid,
                kubernetesPodName: podName));
        }

        private static AiRuntimeLifecycleEvent CreateLifecycleEvent(
            string eventId,
            string eventType,
            DateTimeOffset timestampUtc,
            string controlPlaneId,
            string? runtimeInstanceId = null,
            string? runtimeId = null,
            string? poolId = null,
            string? hostId = null,
            string? kubernetesPodUid = null,
            string? kubernetesPodName = null,
            string? tenantId = null,
            string? sharedRunId = null,
            string? localRunId = null,
            string? executionId = null,
            string? runtimeFailureIncidentId = null,
            string? ledgerEntryId = null,
            string? forensicsId = null,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId = eventId,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                ControlPlaneId = controlPlaneId,
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                HostId = hostId,
                KubernetesPodUid = kubernetesPodUid,
                KubernetesNamespace = kubernetesPodName is null ? null : "multiplexed-ai",
                KubernetesPodName = kubernetesPodName,
                KubernetesNodeName = kubernetesPodName is null ? null : "node-a",
                RuntimeInstanceId = runtimeInstanceId,
                RuntimeId = runtimeId,
                TenantId = tenantId,
                SharedRunId = sharedRunId,
                LocalRunId = localRunId,
                ExecutionId = executionId,
                RuntimeFailureIncidentId = runtimeFailureIncidentId,
                LedgerEntryId = ledgerEntryId,
                ForensicsId = forensicsId,
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static AiRuntimeInstanceSnapshot CreateSnapshot(
            string runtimeInstanceId,
            string controlPlaneId,
            string? poolId,
            string? hostId,
            string? runtimeId,
            AiRuntimeInstanceStatus status,
            string? hostName = null,
            int? processId = null,
            string? kubernetesNamespace = null,
            string? kubernetesPodName = null,
            string? kubernetesNodeName = null)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                ControlPlaneId = controlPlaneId,
                PoolId = poolId,
                HostId = hostId,
                RuntimeId = runtimeId,
                Status = status,
                HostName = hostName,
                ProcessId = processId,
                KubernetesNamespace = kubernetesNamespace,
                KubernetesPodName = kubernetesPodName,
                KubernetesNodeName = kubernetesNodeName,
                WorkerCount = 1,
                RegisteredAtUtc = now,
                LastHeartbeatAtUtc = now,
                SnapshotAtUtc = now
            };
        }

        private sealed class StubLifecycleJournal : IAiRuntimeLifecycleJournal
        {
            private readonly IReadOnlyList<AiRuntimeLifecycleEvent> _events;

            public StubLifecycleJournal(
                IReadOnlyCollection<AiRuntimeLifecycleEvent> events)
            {
                _events = events.ToArray();
            }

            public int ListByControlPlaneCallCount { get; private set; }

            public int ListByIncidentCallCount { get; private set; }

            public int ListByRuntimeCallCount { get; private set; }

            public Task AppendAsync(AiRuntimeLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiRuntimeLifecycleEvent?> GetByEventIdAsync(string eventId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByControlPlaneIdAsync(
                string controlPlaneId,
                CancellationToken cancellationToken = default)
            {
                ListByControlPlaneCallCount++;

                return Task.FromResult<IReadOnlyList<AiRuntimeLifecycleEvent>>(
                    _events
                        .Where(item => string.Equals(item.ControlPlaneId, controlPlaneId, StringComparison.Ordinal))
                        .ToArray());
            }

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByPoolIdAsync(string poolId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByHostIdAsync(string hostId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByKubernetesPodUidAsync(string kubernetesPodUid, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeInstanceIdAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ListByRuntimeCallCount++;

                return Task.FromResult<IReadOnlyList<AiRuntimeLifecycleEvent>>(
                    _events
                        .Where(item => string.Equals(
                            item.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.Ordinal))
                        .ToArray());
            }

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByRuntimeFailureIncidentIdAsync(
                string runtimeFailureIncidentId,
                CancellationToken cancellationToken = default)
            {
                ListByIncidentCallCount++;

                return Task.FromResult<IReadOnlyList<AiRuntimeLifecycleEvent>>(
                    _events
                        .Where(item => string.Equals(
                            item.RuntimeFailureIncidentId,
                            runtimeFailureIncidentId,
                            StringComparison.Ordinal))
                        .ToArray());
            }

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListBySharedRunIdAsync(string tenantId, string sharedRunId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<IReadOnlyList<AiRuntimeLifecycleEvent>> ListByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
