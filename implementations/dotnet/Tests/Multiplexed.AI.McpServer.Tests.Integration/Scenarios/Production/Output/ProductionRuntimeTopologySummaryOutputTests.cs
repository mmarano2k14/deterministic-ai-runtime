using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

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
        public void BuildParallel_Should_Write_One_Final_Grouped_Section()
        {
            var summary =
                ProductionRuntimeTopologySummaryOutput.BuildParallel(
                    new[]
                    {
                        "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY\nControlPlaneId='control-plane-b'",
                        "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY\nControlPlaneId='control-plane-a'"
                    },
                    expectedScenarioCount: 2);

            Assert.Equal(
                1,
                summary.Split(
                    "# PARALLEL RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY",
                    StringSplitOptions.None).Length - 1);
            Assert.Contains("CapturedScenarioCount='2'", summary, StringComparison.Ordinal);
            Assert.Contains("MissingScenarioCount='0'", summary, StringComparison.Ordinal);
            Assert.True(
                summary.IndexOf("control-plane-a", StringComparison.Ordinal) <
                summary.IndexOf("control-plane-b", StringComparison.Ordinal));
            Assert.Equal(
                2,
                summary.Split(
                    "## SCENARIO RUNTIME TOPOLOGY AND RUN PLACEMENT",
                    StringSplitOptions.None).Length - 1);
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
    }
}
