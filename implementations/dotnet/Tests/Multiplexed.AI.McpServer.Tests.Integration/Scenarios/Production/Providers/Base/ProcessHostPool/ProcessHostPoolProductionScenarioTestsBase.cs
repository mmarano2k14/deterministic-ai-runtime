using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.Stores;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Runs one transport-neutral production scenario over the local hierarchy equivalent to
    /// KubernetesPool: several independent parent Process Hosts, each owning several independently
    /// addressable runtime child processes.
    /// </summary>
    public abstract class ProcessHostPoolProductionScenarioTestsBase
    {
        private const int StepCount = 50;
        private const int KillAfterCompletedStepCount = 25;
        private const int MaximumAdmissionAttemptCount = 8;
        private const int BoundaryFailureCrashCheckpointStateTtlMinutes = 30;
        private const int BoundaryFailureAdmissionBackpressureTimeoutMinutes = 5;
        private const int ExternalBoundaryFailureWaitTimeoutMinutes = 15;
        private const string RequestedBy =
            "mcp-process-host-pool-production-proof";
        private const string Source =
            "process-host-pool-production-proof";

        private readonly ITestOutputHelper output;
        private readonly ProcessHostPoolProductionScenarioProfile profile;

        /// <summary>
        /// Initializes the shared multi-host ProcessPool production proof.
        /// </summary>
        protected ProcessHostPoolProductionScenarioTestsBase(
            ITestOutputHelper output,
            ProcessHostPoolProductionScenarioProfile profile)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        /// <summary>
        /// Executes bounded capacity without injecting a parent-host failure.
        /// </summary>
        protected Task ExecuteBoundedCapacityMachineLimitScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount)
        {
            return this.ExecuteScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount: 1,
                injectChildRuntimeFailure: false,
                injectParentHostFailure: false);
        }

        /// <summary>
        /// Force-kills one busy parent Process Host and proves exact membership recovery.
        /// </summary>
        protected Task ExecuteForcedParentHostFailureScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount)
        {
            return this.ExecuteScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount: 1,
                injectChildRuntimeFailure: false,
                injectParentHostFailure: true);
        }

        /// <summary>
        /// Executes repeated parent-host failure cycles against one warm control plane and pool.
        /// Cleanup occurs only after the final cycle.
        /// </summary>
        protected Task ExecuteWarmReuseScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return this.ExecuteScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: false,
                injectParentHostFailure: true);
        }

        /// <summary>
        /// Executes the final hierarchical ProcessHostPool proof: one exact child runtime is
        /// killed after durable DAG progress, its parent and siblings survive, then one distinct
        /// busy parent Process Host and its complete runtime tree are force-killed. The same warm
        /// pool remains alive across every requested cycle and is cleaned only after the final one.
        /// </summary>
        protected Task ExecuteFullFailureProductionScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            return this.ExecuteScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: true,
                injectParentHostFailure: true);
        }

        /// <summary>
        /// Executes the same final hierarchical ProcessHostPool proof, but leaves the distinct
        /// busy parent Process Host alive until an operator kills its exact process tree externally.
        /// The test waits for that exact parent incarnation to exit before running the unchanged
        /// suppression, replacement, recovery, warm-reuse, replay, ledger, and cleanup proof.
        /// Keep the manual gate watcher open in a separate PowerShell window:
        /// <code>Get-Content "$env:TEMP\multiplexed-ai-manual-processhost-kill.txt" -Wait</code>
        /// </summary>
        protected Task ExecuteFullFailureProductionScenarioAwaitExternalParentFailureAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount)
        {
            var signalPath =
                ManualExternalFailureGateSignal.PrepareProcessHostWatch();

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} MANUAL EXTERNAL FAILURE WATCH] TargetKind='ProcessHost', PowerShellCommand='{ManualExternalFailureGateSignal.ProcessHostPowerShellWatchCommand}', SignalFile='{signalPath}', Instruction='Keep this watcher open for every cycle.'");

            return this.ExecuteScenarioAsync(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                injectChildRuntimeFailure: true,
                injectParentHostFailure: true,
                waitForExternalParentHostFailure: true);
        }

        private async Task ExecuteScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount,
            bool injectChildRuntimeFailure,
            bool injectParentHostFailure,
            bool waitForExternalParentHostFailure = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumProcessHostCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                runtimeCountPerHost);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                submissionIterationCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                executionCycleCount);

            if (waitForExternalParentHostFailure &&
                !injectParentHostFailure)
            {
                throw new ArgumentException(
                    "External parent-host failure waiting requires parent-host failure injection to be enabled.",
                    nameof(waitForExternalParentHostFailure));
            }

            if (injectChildRuntimeFailure)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(
                    maximumProcessHostCount,
                    3);
                ArgumentOutOfRangeException.ThrowIfLessThan(
                    runtimeCountPerHost,
                    2);

                if (executionCycleCount < 2)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(executionCycleCount),
                        executionCycleCount,
                        "The final ProcessHostPool warm-reuse proof requires at least two sequential execution cycles.");
                }

                if (injectParentHostFailure &&
                    submissionIterationCount < 2)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(submissionIterationCount),
                        submissionIterationCount,
                        "The final hierarchical failure proof requires at least two full-capacity waves so the last configured wave can be deferred until after child-runtime recovery.");
                }
            }

            var totalRuntimeCount =
                checked(maximumProcessHostCount * runtimeCountPerHost);
            var submittedRunCountPerCycle =
                checked(totalRuntimeCount * submissionIterationCount);
            var logicalStepCountPerCycle =
                checked(submittedRunCountPerCycle * StepCount);
            var workloadNoProgressTimeout =
                ResolveWorkloadNoProgressTimeout(
                    submittedRunCountPerCycle);

            var scenario =
                CreateScenario(
                    totalRuntimeCount,
                    submittedRunCountPerCycle,
                    injectChildRuntimeFailure,
                    injectParentHostFailure,
                    executionCycleCount);
            var tenant = Assert.Single(scenario.Tenants);
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);
            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver
                    .ResolveRuntimeHostAssemblyPath();
            var poolId =
                RuntimePoolCrashRecoveryScenarioIdentity.CreatePoolId(
                    this.profile.PoolIdPrefix,
                    controlPlaneId);
            var controlPlaneSettings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildControlPlaneSettings(
                        this.profile,
                        scenario,
                        controlPlaneId,
                        runtimeHostAssemblyPath,
                        totalRuntimeCount);

            await using var dataStoreTrafficObserver =
                await ProductionDataStoreTrafficObserver
                    .StartAsync(this.output)
                    .ConfigureAwait(false);

            this.WriteScenarioHeader(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                totalRuntimeCount,
                submittedRunCountPerCycle,
                logicalStepCountPerCycle,
                controlPlaneId,
                poolId,
                injectChildRuntimeFailure,
                injectParentHostFailure,
                waitForExternalParentHostFailure);

            var scenarioStopwatch = Stopwatch.StartNew();

            this.WritePhase(
                phaseNumber: 1,
                cycleNumber: null,
                title:
                    "SETUP DURABLE CONTROL PLANE AND PROCESS HOST POOL",
                passTarget:
                    "Start the durable Mongo/Redis control plane, tenant MCP client, and exact ProcessHostCount × RuntimeCountPerHost topology for this ControlPlaneId and PoolId.");

            var setupStopwatch = Stopwatch.StartNew();
            var setupDuration = TimeSpan.Zero;
            Stopwatch? finalProofStopwatch = null;
            var finalProofDuration = TimeSpan.Zero;

            await using var cluster =
                await ProcessHostPoolProductionCluster
                    .StartAsync(
                        this.profile,
                        controlPlaneSettings,
                        controlPlaneId,
                        poolId,
                        runtimeHostAssemblyPath,
                        maximumProcessHostCount,
                        runtimeCountPerHost,
                        tenant,
                        this.output,
                        timeoutPerHost: TimeSpan.FromMinutes(5))
                    .ConfigureAwait(false);

            await using var controlPlaneHost =
                new GenericMcpServerTestHost(controlPlaneSettings);

            _ = controlPlaneHost.Services;

            var registry =
                controlPlaneHost.Services.GetRequiredService<
                    IAiRuntimeInstanceRegistry>();
            var sharedRunStore =
                controlPlaneHost.Services.GetRequiredService<
                    IAiSharedRunStore>();
            var sharedQueue =
                controlPlaneHost.Services.GetRequiredService<
                    IAiSharedQueue>();
            var runExecutionIndex =
                controlPlaneHost.Services.GetRequiredService<
                    IAiRuntimeRunExecutionIndex>();
            var dagStore =
                controlPlaneHost.Services.GetRequiredService<
                    IAiDagExecutionStore>();
            var forensicsQueryService =
                controlPlaneHost.Services.GetRequiredService<
                    IAiRuntimeRecoveryForensicsQueryService>();
            var lifecycleJournal =
                controlPlaneHost.Services.GetRequiredService<
                    IAiRuntimeLifecycleJournal>();
            var redisConnection =
                controlPlaneHost.Services.GetRequiredService<
                    IConnectionMultiplexer>();
            var recoveryCoordinator =
                new ProcessHostPoolProductionRecoveryCoordinator(
                    controlPlaneHost.Services,
                    this.output,
                    this.profile.LogPrefix);

            using var tenantHttpClient = controlPlaneHost.CreateClient();
            tenantHttpClient.Timeout = TimeSpan.FromMinutes(30);

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        controlPlaneHost,
                        tenantHttpClient,
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            IReadOnlySet<string>? previousFinalHostIds = null;
            IReadOnlySet<int>? previousFinalParentProcessIds = null;
            IReadOnlySet<string>? previousFinalRuntimeInstanceIds = null;

            var totalSubmittedRunCount = 0;
            var totalCompletedRunCount = 0;
            var totalReplayProofCount = 0;
            var totalRecoveredRunCount = 0;
            var totalChildRuntimeCrashCount = 0;
            var totalParentHostCrashCount = 0;
            var totalAdmissionTooManyRequestsRetryCount = 0;
            var totalReplayTooManyRequestsRetryCount = 0;
            var totalExecutionLedgerEntryCount = 0;
            var totalControlPlaneLedgerEntryCount = 0;
            var totalRuntimeLifecycleLedgerEntryCount = 0;
            var totalRawStepCompletedLedgerEntryCount = 0;
            var totalDistinctLogicalStepCompletedLedgerCount = 0;
            var totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount = 0;
            var totalRecoveryForensicsCount = 0;
            var allRunPlacements =
                new List<ProductionRuntimeRunPlacement>();
            var historicalRuntimeSnapshots =
                new Dictionary<string, AiRuntimeInstanceSnapshot>(
                    StringComparer.Ordinal);
            var cycleTimings =
                new List<ProcessHostPoolProductionCycleTiming>();

            for (var cycleNumber = 1;
                 cycleNumber <= executionCycleCount;
                 cycleNumber++)
            {
                var cycleStopwatch = Stopwatch.StartNew();
                var cycleLedgerFromUtc = DateTimeOffset.UtcNow.AddSeconds(-5);

                var cycleStartTopology =
                    await WaitForExactTopologyAsync(
                            registry,
                            cluster,
                            controlPlaneId,
                            this.profile.ProviderName,
                            requireAvailableCapacity: true,
                            TimeSpan.FromMinutes(3))
                        .ConfigureAwait(false);

                CaptureRuntimeTopologyHistory(
                    historicalRuntimeSnapshots,
                    cycleStartTopology);

                if (cycleNumber == 1)
                {
                    setupStopwatch.Stop();
                    setupDuration = setupStopwatch.Elapsed;

                    this.output.WriteLine(
                        $"[{this.profile.LogPrefix} TIMING] Phase='Setup durable control plane, tenant MCP client, parent Process Hosts, and exact runtime topology', Duration='{setupDuration}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");
                }

                var cycleStartHostIds = ReadHostIds(cluster);
                var cycleStartParentProcessIds = ReadParentProcessIds(cluster);
                var cycleStartRuntimeInstanceIds =
                    ReadRuntimeInstanceIds(cycleStartTopology);

                if (previousFinalHostIds is not null)
                {
                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        previousFinalHostIds,
                        cycleStartHostIds,
                        $"{this.profile.LogPrefix} warm reuse cycle {cycleNumber} parent HostId proof");

                    Assert.True(
                        previousFinalParentProcessIds!.SetEquals(
                            cycleStartParentProcessIds),
                        $"{this.profile.LogPrefix} warm reuse cycle {cycleNumber} changed one or more parent ProcessIds before failure injection.");

                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        previousFinalRuntimeInstanceIds!,
                        cycleStartRuntimeInstanceIds,
                        $"{this.profile.LogPrefix} warm reuse cycle {cycleNumber} runtime identity proof");
                }

                WriteTopology(
                    $"CYCLE {cycleNumber} START",
                    cluster,
                    cycleStartTopology,
                    this.output,
                    this.profile.LogPrefix);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} WARM REUSE] Cycle='{cycleNumber}', ColdStart='{cycleNumber == 1}', ProcessHostCount='{cluster.Hosts.Count}', RuntimeCount='{cycleStartRuntimeInstanceIds.Count}', PoolId='{cluster.PoolId}'.");

                this.WritePhase(
                    phaseNumber: 2,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title: "SUBMIT FULL-CAPACITY WAVES",
                    passTarget:
                        "Submit every run through a dynamically bounded MCP producer, honor transient 429 backpressure, and persist every logical run through QueueFirst admission without waiting for DAG completion.");

                var deferBoundaryFailureWave =
                    injectChildRuntimeFailure && injectParentHostFailure;
                var initialSubmissionIterationCount =
                    deferBoundaryFailureWave
                        ? submissionIterationCount - 1
                        : submissionIterationCount;

                var submissionStopwatch = Stopwatch.StartNew();
                var admission =
                    await RuntimePoolProductionCycleExecutor
                        .SubmitQueueFirstWavesAsync(
                            mcp,
                            tenant,
                            scenario.Name,
                            controlPlaneId,
                            RequestedBy,
                            Source,
                            runsPerIteration: totalRuntimeCount,
                            submissionIterationCount:
                                initialSubmissionIterationCount,
                            maximumConcurrentSubmissions:
                                Math.Clamp(totalRuntimeCount, 4, 16),
                            maximumAdmissionAttemptCount:
                                MaximumAdmissionAttemptCount,
                            cycleNumber:
                                executionCycleCount > 1
                                    ? cycleNumber
                                    : null,
                            startingIterationNumber: 1)
                        .ConfigureAwait(false);
                submissionStopwatch.Stop();

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} MCP ADMISSION INITIAL] Cycle='{cycleNumber}', SubmittedRunCount='{admission.SharedRunIds.Count}', FullCapacityWaveCount='{initialSubmissionIterationCount}', ConfiguredFullCapacityWaveCount='{submissionIterationCount}', DeferredBoundaryFailureWaveCount='{(deferBoundaryFailureWave ? 1 : 0)}', RunsPerWave='{totalRuntimeCount}', TooManyRequestsRetryCount='{admission.TooManyRequestsRetryCount}'.");
                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='Submit initial full-capacity waves', Duration='{submissionStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                this.WritePhase(
                    phaseNumber: 3,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title:
                        injectChildRuntimeFailure
                            ? waitForExternalParentHostFailure
                                ? "FORCE-KILL ONE CHILD RUNTIME, THEN WAIT FOR ONE DISTINCT BUSY PARENT HOST TO BE KILLED EXTERNALLY, RECOVER, AND DRAIN EVERY DAG"
                                : "FORCE-KILL ONE CHILD RUNTIME, THEN ONE DISTINCT BUSY PARENT HOST, RECOVER, AND DRAIN EVERY DAG"
                            : injectParentHostFailure
                                ? waitForExternalParentHostFailure
                                    ? "WAIT FOR ONE BUSY PARENT HOST TO BE KILLED EXTERNALLY, RECOVER, AND DRAIN EVERY DAG"
                                    : "FORCE-KILL ONE BUSY PARENT HOST, RECOVER, AND DRAIN EVERY DAG"
                                : "DRAIN EVERY DAG WITHOUT FAILURE INJECTION",
                    passTarget:
                        injectChildRuntimeFailure
                            ? waitForExternalParentHostFailure
                                ? "Kill one exact child runtime after durable DAG progress while preserving its parent and siblings, recover that work once, then expose one distinct fully busy parent Process Host and wait for an operator to kill its exact process tree before running the unchanged recovery proof."
                                : "Kill one exact child runtime after durable DAG progress while preserving its parent and siblings, recover that work once, then kill one distinct fully busy parent Process Host, replace its complete membership, and drain every DAG."
                            : injectParentHostFailure
                                ? waitForExternalParentHostFailure
                                    ? "Expose one fully busy parent Process Host, wait for an operator to kill its exact process tree, then suppress that exact membership, start one fresh replacement parent, recover only impacted runs once, and complete all 50 DAG steps."
                                    : "Kill one parent Process Host only after its runtimes own active work, suppress that exact membership, start one fresh replacement parent, recover only impacted runs once, and complete all 50 DAG steps."
                                : "Use the full bounded ProcessHostPool capacity, preserve every parent and runtime identity, and complete every submitted DAG with exactly 50 logical steps.");

                var drainStopwatch = Stopwatch.StartNew();
                ProcessHostPoolChildRuntimeFailureTarget?
                    childRuntimeFailureTarget = null;
                RealRuntimeCrashFailedRuntimeRecoveryProof?
                    childRuntimeRecoveryProof = null;
                IReadOnlyList<AiRuntimeRecoveryForensicsReadModel>
                    childRuntimeRecoveryForensics =
                        Array.Empty<AiRuntimeRecoveryForensicsReadModel>();
                ProcessHostPoolProductionFailureTarget? failureTarget = null;
                ProcessHostPoolProductionRecoveryProof? recoveryProof = null;
                ProductionCrashCheckpointGate? boundaryFailureCrashGate = null;
                var excludedParentHostIds =
                    new HashSet<string>(StringComparer.Ordinal);
                IReadOnlySet<string> parentFailureCandidateSharedRunIds =
                    admission.SharedRunIds;

                if (injectChildRuntimeFailure)
                {
                    childRuntimeFailureTarget =
                        await WaitForBusyChildRuntimeFailureTargetAsync(
                                registry,
                                sharedRunStore,
                                runExecutionIndex,
                                cluster,
                                admission.SharedRunIds,
                                controlPlaneId,
                                tenant.TenantId,
                                TimeSpan.FromMinutes(5))
                            .ConfigureAwait(false);

                    var childInventory =
                        CreateChildRuntimeFailureInventory(
                            tenant,
                            mcp,
                            childRuntimeFailureTarget);
                    var childProcessControl =
                        new ProcessHostPoolChildRuntimeProcessControl(
                            registry,
                            cluster.PoolId,
                            childRuntimeFailureTarget.Host.HostId,
                            childRuntimeFailureTarget.Host.ProcessId,
                            this.output,
                            this.profile.LogPrefix);

                    childRuntimeRecoveryProof =
                        await ProductionRealRuntimeCrashRecoveryTestHelpers
                            .KillRuntimeAndRecoverAssignedInventoryAsync(
                                this.output,
                                childProcessControl,
                                registry,
                                runExecutionIndex,
                                sharedRunStore,
                                sharedQueue,
                                dagStore,
                                childInventory,
                                minimumCompletedStepsBeforeKill:
                                    KillAfterCompletedStepCount,
                                progressTimeout: TimeSpan.FromMinutes(3),
                                unsafeTimeout: TimeSpan.FromMinutes(3),
                                requeueTimeout: TimeSpan.FromMinutes(2),
                                redispatchTimeout: TimeSpan.FromMinutes(3),
                                executionResolveTimeout:
                                    TimeSpan.FromMinutes(2),
                                observationMode:
                                    ProductionRecoveryObservationMode.Polling,
                                runtimeTenantOwnershipAssertion:
                                    AssertRuntimeBelongsToTenantAsync,
                                unsafeRuntimeRecoveryTrigger:
                                    () => recoveryCoordinator
                                        .RecoverChildRuntimeAsync(
                                            cluster,
                                            childRuntimeFailureTarget.Host,
                                            childRuntimeFailureTarget.Runtime.RuntimeInstanceId,
                                            childRuntimeFailureTarget.ActiveRun.SharedRunId,
                                            childRuntimeFailureTarget.ActiveRun.LocalRunId,
                                            childRuntimeFailureTarget.ActiveRun.ExecutionId,
                                            cycleNumber,
                                            $"mcp-{this.profile.ProviderName}-process-host-pool-child-cycle-{cycleNumber}",
                                            TimeSpan.FromMinutes(3)))
                            .ConfigureAwait(false);

                    childRuntimeRecoveryForensics =
                        await ProductionRealRuntimeCrashRecoveryTestHelpers
                            .AssertRecoveredInventoryForensicsAsync(
                                this.output,
                                forensicsQueryService,
                                childRuntimeRecoveryProof,
                                TimeSpan.FromMinutes(3))
                            .ConfigureAwait(false);

                    var childRecoveredWork =
                        Assert.Single(childRuntimeRecoveryProof.RecoveredWorks);
                    var childRecoveryRuntime =
                        (await registry
                                .ListAsync(includeStopped: false)
                                .ConfigureAwait(false))
                            .Single(
                                runtime => StringComparer.Ordinal.Equals(
                                    runtime.RuntimeInstanceId,
                                    childRecoveredWork.ReplacementRuntimeInstanceId));

                    Assert.False(
                        string.IsNullOrWhiteSpace(childRecoveryRuntime.HostId));
                    excludedParentHostIds.Add(
                        childRuntimeFailureTarget.Host.HostId);
                    excludedParentHostIds.Add(
                        childRecoveryRuntime.HostId!);

                    await cluster
                        .RefreshHostRuntimeMembershipAsync(
                            childRuntimeFailureTarget.Host.HostId,
                            TimeSpan.FromMinutes(3))
                        .ConfigureAwait(false);

                    var childReplacementTopology =
                        await WaitForExactTopologyAsync(
                                registry,
                                cluster,
                                controlPlaneId,
                                this.profile.ProviderName,
                                requireAvailableCapacity: false,
                                TimeSpan.FromMinutes(3))
                            .ConfigureAwait(false);

                    AssertExactChildRuntimeReplacementTopology(
                        cluster,
                        childReplacementTopology,
                        childRuntimeFailureTarget,
                        childRuntimeRecoveryProof,
                        this.profile.LogPrefix,
                        cycleNumber);

                    WriteTopology(
                        $"CYCLE {cycleNumber} CHILD RUNTIME REPLACEMENT",
                        cluster,
                        childReplacementTopology,
                        this.output,
                        this.profile.LogPrefix);

                    CaptureRuntimeTopologyHistory(
                        historicalRuntimeSnapshots,
                        childReplacementTopology);

                    totalChildRuntimeCrashCount =
                        checked(totalChildRuntimeCrashCount + 1);
                    totalRecoveredRunCount =
                        checked(
                            totalRecoveredRunCount +
                            childRuntimeRecoveryProof.RecoveredWorks.Count);
                    totalRecoveryForensicsCount =
                        checked(
                            totalRecoveryForensicsCount +
                            childRuntimeRecoveryForensics.Count);

                    if (deferBoundaryFailureWave)
                    {
                        this.output.WriteLine(
                            $"[{this.profile.LogPrefix} HIERARCHICAL FAILURE GATE] Cycle='{cycleNumber}', State='waiting-for-initial-child-failure-workload-drain', InitialWaveCount='{initialSubmissionIterationCount}', DeferredWaveNumber='{submissionIterationCount}'.");

                        _ = await RuntimePoolProductionWorkloadObserver
                            .WaitForSubmittedRunsToCompleteAsync(
                                sharedRunStore,
                                runExecutionIndex,
                                dagStore,
                                admission.SharedRunIds,
                                controlPlaneId,
                                tenant.TenantId,
                                scenario.CompletionTimeout,
                                noProgressTimeout: workloadNoProgressTimeout)
                            .ConfigureAwait(false);

                        var boundaryFailureFillerRunCount =
                            checked(
                                totalRuntimeCount -
                                runtimeCountPerHost);
                        var boundaryFailureTargetRunStartNumber =
                            checked(boundaryFailureFillerRunCount + 1);

                        if (boundaryFailureFillerRunCount > 0)
                        {
                            submissionStopwatch.Start();
                            var boundaryFailureFillerAdmission =
                                await RuntimePoolProductionCycleExecutor
                                    .SubmitQueueFirstWavesAsync(
                                        mcp,
                                        tenant,
                                        scenario.Name,
                                        controlPlaneId,
                                        RequestedBy,
                                        Source,
                                        runsPerIteration:
                                            boundaryFailureFillerRunCount,
                                        submissionIterationCount: 1,
                                        maximumConcurrentSubmissions:
                                            Math.Clamp(
                                                boundaryFailureFillerRunCount,
                                                4,
                                                16),
                                        maximumAdmissionAttemptCount:
                                            MaximumAdmissionAttemptCount,
                                        cycleNumber:
                                            executionCycleCount > 1
                                                ? cycleNumber
                                                : null,
                                        startingIterationNumber:
                                            submissionIterationCount,
                                        admissionBackpressureTimeout:
                                            TimeSpan.FromMinutes(
                                                BoundaryFailureAdmissionBackpressureTimeoutMinutes),
                                        startingRunNumber: 1)
                                    .ConfigureAwait(false);
                            submissionStopwatch.Stop();

                            admission =
                                RuntimePoolProductionCycleExecutor
                                    .CombineAdmissionProofs(
                                        admission,
                                        boundaryFailureFillerAdmission);

                            this.output.WriteLine(
                                $"[{this.profile.LogPrefix} HIERARCHICAL FAILURE FILLER] " +
                                $"Cycle='{cycleNumber}', " +
                                $"WaveNumber='{submissionIterationCount}', " +
                                $"SubmittedRunCount='{boundaryFailureFillerAdmission.SharedRunIds.Count}', " +
                                "CrashCheckpoint='none', " +
                                "Placement='unconstrained', " +
                                $"TooManyRequestsRetryCount='{boundaryFailureFillerAdmission.TooManyRequestsRetryCount}'.");

                            _ = await RuntimePoolProductionWorkloadObserver
                                .WaitForSubmittedRunsToCompleteAsync(
                                    sharedRunStore,
                                    runExecutionIndex,
                                    dagStore,
                                    boundaryFailureFillerAdmission.SharedRunIds,
                                    controlPlaneId,
                                    tenant.TenantId,
                                    scenario.CompletionTimeout,
                                    noProgressTimeout: workloadNoProgressTimeout)
                                .ConfigureAwait(false);
                        }

                        var boundaryFailureTopology =
                            await WaitForExactTopologyAsync(
                                    registry,
                                    cluster,
                                    controlPlaneId,
                                    this.profile.ProviderName,
                                    requireAvailableCapacity: true,
                                    TimeSpan.FromMinutes(3))
                                .ConfigureAwait(false);

                        var boundaryFailureTarget =
                            cluster
                                .Hosts
                                .OrderBy(host => host.Ordinal)
                                .Where(
                                    host =>
                                        !excludedParentHostIds.Contains(
                                            host.HostId))
                                .Select(
                                    host => new
                                    {
                                        Host = host,
                                        Members =
                                            boundaryFailureTopology
                                                .Where(
                                                    runtime =>
                                                        StringComparer.Ordinal.Equals(
                                                            runtime.HostId,
                                                            host.HostId) &&
                                                        runtime.Status ==
                                                            AiRuntimeInstanceStatus.Ready &&
                                                        runtime.CanAcceptRun)
                                                .OrderBy(
                                                    runtime =>
                                                        runtime.RuntimeInstanceId,
                                                    StringComparer.Ordinal)
                                                .ToArray()
                                    })
                                .FirstOrDefault(
                                    candidate =>
                                        candidate.Members.Length ==
                                        runtimeCountPerHost);

                        if (boundaryFailureTarget is null)
                        {
                            throw new InvalidOperationException(
                                "No distinct fully available parent Process Host remained for the deterministic boundary failure wave.");
                        }

                        var boundaryFailureTargetRuntimeInstanceIds =
                            boundaryFailureTarget
                                .Members
                                .Select(
                                    runtime =>
                                        runtime.RuntimeInstanceId)
                                .ToArray();

                        Assert.Equal(
                            runtimeCountPerHost,
                            boundaryFailureTargetRuntimeInstanceIds.Length);
                        Assert.Equal(
                            runtimeCountPerHost,
                            boundaryFailureTargetRuntimeInstanceIds
                                .Distinct(StringComparer.Ordinal)
                                .Count());

                        this.output.WriteLine(
                            $"[{this.profile.LogPrefix} HIERARCHICAL FAILURE TARGET] " +
                            $"Cycle='{cycleNumber}', " +
                            $"TargetHostId='{boundaryFailureTarget.Host.HostId}', " +
                            $"TargetRuntimeCount='{runtimeCountPerHost}', " +
                            $"CompletedFillerRunCount='{boundaryFailureFillerRunCount}', " +
                            $"TargetRunStartNumber='{boundaryFailureTargetRunStartNumber}'.");

                        boundaryFailureCrashGate =
                            await ProductionCrashCheckpointGate
                                .ArmAsync(
                                    redisConnection,
                                    this.output,
                                    controlPlaneId,
                                    tenant.TenantId,
                                    $"{scenario.Name}-cycle-{cycleNumber:000}-boundary-wave-{submissionIterationCount:000}",
                                    checkpointStepIndex:
                                        KillAfterCompletedStepCount + 1,
                                    stateTtl:
                                        TimeSpan.FromMinutes(
                                            BoundaryFailureCrashCheckpointStateTtlMinutes))
                                .ConfigureAwait(false);

                        RuntimePoolProductionCycleAdmissionProof
                            boundaryFailureAdmission;

                        try
                        {
                            submissionStopwatch.Start();
                            boundaryFailureAdmission =
                                await RuntimePoolProductionCycleExecutor
                                    .SubmitQueueFirstWavesAsync(
                                        mcp,
                                        tenant,
                                        scenario.Name,
                                        controlPlaneId,
                                        RequestedBy,
                                        Source,
                                        runsPerIteration:
                                            runtimeCountPerHost,
                                        submissionIterationCount: 1,
                                        maximumConcurrentSubmissions: 1,
                                        maximumAdmissionAttemptCount:
                                            MaximumAdmissionAttemptCount,
                                        cycleNumber:
                                            executionCycleCount > 1
                                                ? cycleNumber
                                                : null,
                                        startingIterationNumber:
                                            submissionIterationCount,
                                        crashCheckpoint:
                                            boundaryFailureCrashGate.Definition,
                                        admissionBackpressureTimeout:
                                            TimeSpan.FromMinutes(
                                                BoundaryFailureAdmissionBackpressureTimeoutMinutes),
                                        placementFactory:
                                            (_, runNumber) =>
                                                new AiRunPlacementDirective
                                                {
                                                    Target =
                                                        new AiRunPlacementTarget
                                                        {
                                                            RuntimeInstanceId =
                                                                boundaryFailureTargetRuntimeInstanceIds[
                                                                    runNumber -
                                                                    boundaryFailureTargetRunStartNumber]
                                                        },
                                                    Requirement =
                                                        AiRunPlacementRequirement.Required,
                                                    Fallback =
                                                        AiRunPlacementFallback.Reject
                                                },
                                        startingRunNumber:
                                            boundaryFailureTargetRunStartNumber)
                                    .ConfigureAwait(false);
                            submissionStopwatch.Stop();

                            await boundaryFailureCrashGate
                                .WaitUntilReachedAsync(
                                    TimeSpan.FromMinutes(3))
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            if (submissionStopwatch.IsRunning)
                            {
                                submissionStopwatch.Stop();
                            }

                            await boundaryFailureCrashGate
                                .ReleaseAsync()
                                .ConfigureAwait(false);
                            boundaryFailureCrashGate = null;
                            throw;
                        }

                        var parentFailureTargetAdmissionResults =
                            boundaryFailureAdmission
                                .Results
                                .ToArray();

                        Assert.Equal(
                            runtimeCountPerHost,
                            parentFailureTargetAdmissionResults.Length);
                        Assert.All(
                            parentFailureTargetAdmissionResults,
                            result => Assert.False(
                                string.IsNullOrWhiteSpace(
                                    result.SharedRunId)));

                        parentFailureCandidateSharedRunIds =
                            parentFailureTargetAdmissionResults
                                .Select(result => result.SharedRunId!)
                                .ToHashSet(StringComparer.Ordinal);

                        Assert.Equal(
                            runtimeCountPerHost,
                            parentFailureCandidateSharedRunIds.Count);

                        admission =
                            RuntimePoolProductionCycleExecutor
                                .CombineAdmissionProofs(
                                    admission,
                                    boundaryFailureAdmission);

                        this.output.WriteLine(
                            $"[{this.profile.LogPrefix} WARM BOUNDARY FAILURE WAVE] " +
                            $"Cycle='{cycleNumber}', " +
                            $"WaveNumber='{submissionIterationCount}', " +
                            $"SubmittedRunCount='{boundaryFailureFillerRunCount + boundaryFailureAdmission.SharedRunIds.Count}', " +
                            $"CompletedFillerRunCount='{boundaryFailureFillerRunCount}', " +
                            $"TargetCheckpointRunCount='{boundaryFailureAdmission.SharedRunIds.Count}', " +
                            $"ReusedRuntimeCapacity='{totalRuntimeCount}', " +
                            $"EligibleDistinctParentCount='{cluster.Hosts.Count - excludedParentHostIds.Count}', " +
                            $"TooManyRequestsRetryCount='{boundaryFailureAdmission.TooManyRequestsRetryCount}'.");
                    }
                }

                Assert.Equal(
                    submittedRunCountPerCycle,
                    admission.SharedRunIds.Count);

                totalSubmittedRunCount =
                    checked(
                        totalSubmittedRunCount +
                        admission.SharedRunIds.Count);
                totalAdmissionTooManyRequestsRetryCount =
                    checked(
                        totalAdmissionTooManyRequestsRetryCount +
                        admission.TooManyRequestsRetryCount);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} MCP ADMISSION CONSOLIDATED] Cycle='{cycleNumber}', SubmittedRunCount='{admission.SharedRunIds.Count}', FullCapacityWaveCount='{submissionIterationCount}', RunsPerWave='{totalRuntimeCount}', BoundaryFailureCandidateRunCount='{parentFailureCandidateSharedRunIds.Count}', TooManyRequestsRetryCount='{admission.TooManyRequestsRetryCount}'.");

                if (injectParentHostFailure)
                {
                    Assert.True(
                        excludedParentHostIds.Count < cluster.Hosts.Count,
                        "The final ProcessHostPool proof excluded every parent boundary before the distinct parent failure could be selected.");

                    try
                    {
                        failureTarget =
                            await WaitForBusyParentHostFailureTargetAsync(
                                    registry,
                                    sharedRunStore,
                                    runExecutionIndex,
                                    cluster,
                                    parentFailureCandidateSharedRunIds,
                                    controlPlaneId,
                                    tenant.TenantId,
                                    TimeSpan.FromMinutes(5),
                                    excludedHostIds:
                                        excludedParentHostIds)
                                .ConfigureAwait(false);

                        recoveryProof =
                            await recoveryCoordinator
                                .RecoverAsync(
                                    cluster,
                                    failureTarget,
                                    cycleNumber,
                                    $"mcp-{this.profile.ProviderName}-process-host-pool-cycle-{cycleNumber}",
                                    TimeSpan.FromMinutes(5),
                                    boundaryFailureCrashGate,
                                    waitForExternalParentHostFailure,
                                    TimeSpan.FromMinutes(
                                        ExternalBoundaryFailureWaitTimeoutMinutes))
                                .ConfigureAwait(false);
                    }
                    finally
                    {
                        if (boundaryFailureCrashGate is not null)
                        {
                            // Idempotent safety release also covers target-selection
                            // or recovery failures before the coordinator reaches the
                            // exact parent termination point.
                            await boundaryFailureCrashGate
                                .ReleaseAsync()
                                .ConfigureAwait(false);
                        }
                    }

                    Assert.Empty(
                        (childRuntimeRecoveryProof?.RecoveredWorks
                             .Select(work => work.Original.SharedRunId) ??
                         Array.Empty<string>())
                            .Intersect(
                                recoveryProof.RecoveredSharedRunIds,
                                StringComparer.Ordinal));

                    totalParentHostCrashCount =
                        checked(totalParentHostCrashCount + 1);
                    totalRecoveredRunCount =
                        checked(
                            totalRecoveredRunCount +
                            recoveryProof.RecoveredSharedRunIds.Count);

                    var replacementTopology =
                        await WaitForExactTopologyAsync(
                                registry,
                                cluster,
                                controlPlaneId,
                                this.profile.ProviderName,
                                requireAvailableCapacity: false,
                                TimeSpan.FromMinutes(3))
                            .ConfigureAwait(false);

                    AssertExactReplacementTopology(
                        cluster,
                        replacementTopology,
                        recoveryProof,
                        this.profile.LogPrefix,
                        cycleNumber);

                    WriteTopology(
                        $"CYCLE {cycleNumber} PARENT HOST REPLACEMENT",
                        cluster,
                        replacementTopology,
                        this.output,
                        this.profile.LogPrefix);

                    CaptureRuntimeTopologyHistory(
                        historicalRuntimeSnapshots,
                        replacementTopology);
                }

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} WORKLOAD OBSERVER] Cycle='{cycleNumber}', ExpectedRunCount='{admission.SharedRunIds.Count}', CompletionTimeout='{scenario.CompletionTimeout}', NoProgressTimeout='{workloadNoProgressTimeout}'.");

                var completedRuns =
                    await RuntimePoolProductionWorkloadObserver
                        .WaitForSubmittedRunsToCompleteAsync(
                            sharedRunStore,
                            runExecutionIndex,
                            dagStore,
                            admission.SharedRunIds,
                            controlPlaneId,
                            tenant.TenantId,
                            scenario.CompletionTimeout,
                            noProgressTimeout: workloadNoProgressTimeout)
                        .ConfigureAwait(false);

                var finalStatuses =
                    await McpTestWaitHelpers
                        .WaitForTerminalRuntimeRunStatusesAsync(
                            mcp,
                            completedRuns,
                            scenario.CompletionTimeout)
                        .ConfigureAwait(false);

                Assert.Equal(submittedRunCountPerCycle, completedRuns.Count);
                Assert.Equal(submittedRunCountPerCycle, finalStatuses.Count);

                Assert.All(
                    completedRuns,
                    run => Assert.False(
                        string.IsNullOrWhiteSpace(
                            run.AssignedRuntimeInstanceId),
                        $"SharedRunId '{run.SharedRunId}' completed without an assigned runtime identity."));

                Assert.All(
                    finalStatuses,
                    status =>
                    {
                        Assert.True(
                            status.Success,
                            status.FailureReason ?? status.Message);
                        Assert.True(
                            string.Equals(
                                status.RunState?.Status,
                                "completed",
                                StringComparison.OrdinalIgnoreCase),
                            $"ProcessHostPool runtime work did not complete. RuntimeInstanceId='{status.RuntimeInstanceId}', RunId='{status.RunId}', ExecutionId='{status.ExecutionId ?? status.RunState?.ExecutionId}', Status='{status.RunState?.Status}', Failure='{status.FailureReason ?? status.RunState?.FailureReason}'.");
                    });

                drainStopwatch.Stop();

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} RUNTIME STATUS PROOF] Cycle='{cycleNumber}', DedicatedRbacContext='true', RunCount='{finalStatuses.Count}'.");
                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='{(injectChildRuntimeFailure ? "Force-kill one child runtime and one distinct busy parent, recover, and drain every DAG" : injectParentHostFailure ? "Force-kill one busy parent, recover, and drain every DAG" : "Drain every DAG without failure injection")}', Duration='{drainStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                this.WritePhase(
                    phaseNumber: 4,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title:
                        injectChildRuntimeFailure
                            ? "BOUNDED CAPACITY AND HIERARCHICAL CHILD/PARENT RECOVERY SAFETY PROOF"
                            : injectParentHostFailure
                                ? "BOUNDED CAPACITY AND EXACT PARENT-HOST RECOVERY SAFETY PROOF"
                                : "BOUNDED CAPACITY AND STABLE TOPOLOGY SAFETY PROOF",
                    passTarget:
                        injectChildRuntimeFailure
                            ? "Prove one exact child replacement preserves its parent and siblings, then prove exact failed-parent membership suppression and replacement without exceeding bounded capacity."
                            : injectParentHostFailure
                                ? "Prove exact failed-parent membership suppression, one fresh replacement parent, bounded active capacity, complete workload drain, and recovery tied only to the injected parent failure."
                                : "Prove parent and runtime identities remain stable, every parent remains alive, active capacity stays within bounds, and the full workload drains.");

                var safetyStopwatch = Stopwatch.StartNew();

                cluster.AssertAllHostsRunning();

                var cycleFinalTopology =
                    await WaitForExactTopologyAsync(
                            registry,
                            cluster,
                            controlPlaneId,
                            this.profile.ProviderName,
                            requireAvailableCapacity: true,
                            TimeSpan.FromMinutes(3))
                        .ConfigureAwait(false);

                var cycleFinalHostIds = ReadHostIds(cluster);
                var cycleFinalParentProcessIds =
                    ReadParentProcessIds(cluster);
                var cycleFinalRuntimeInstanceIds =
                    ReadRuntimeInstanceIds(cycleFinalTopology);

                if (recoveryProof is null)
                {
                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        cycleStartHostIds,
                        cycleFinalHostIds,
                        $"{this.profile.LogPrefix} cycle {cycleNumber} bounded parent HostId reuse proof");
                    Assert.True(
                        cycleStartParentProcessIds.SetEquals(
                            cycleFinalParentProcessIds),
                        $"{this.profile.LogPrefix} cycle {cycleNumber} bounded topology replaced a parent process unexpectedly.");
                    RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                        cycleStartRuntimeInstanceIds,
                        cycleFinalRuntimeInstanceIds,
                        $"{this.profile.LogPrefix} cycle {cycleNumber} bounded runtime identity reuse proof");
                }
                else
                {
                    AssertExactReplacementTopology(
                        cluster,
                        cycleFinalTopology,
                        recoveryProof,
                        this.profile.LogPrefix,
                        cycleNumber);
                }

                CaptureRuntimeTopologyHistory(
                    historicalRuntimeSnapshots,
                    cycleFinalTopology);

                safetyStopwatch.Stop();

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='{(injectChildRuntimeFailure ? "Validate bounded capacity, exact child and parent recovery, workload drain, and hierarchical membership convergence" : injectParentHostFailure ? "Validate bounded capacity, exact parent-host recovery, workload drain, and membership convergence" : "Validate bounded capacity, stable parent/runtime identities, and workload drain")}', Duration='{safetyStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                totalCompletedRunCount =
                    checked(totalCompletedRunCount + finalStatuses.Count);

                this.WritePhase(
                    phaseNumber: 5,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title: "MCP REPLAY LEDGER TRACE AND LIFECYCLE PROOF",
                    passTarget:
                        "Every completed execution must be replayable through MCP with execution ledger, trace, completion, exact step evidence, durable dispatch evidence, lifecycle journal, exact recovery forensics when applicable, and no recovery contamination otherwise.");

                var replayAndLedgerStopwatch = Stopwatch.StartNew();
                var replayTooManyRequestsRetryCount = 0;
                var replayProofs =
                    await RecoveredExecutionReplayProofAssertions
                        .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                            mcp,
                            tenant.TenantId,
                            finalStatuses,
                            RequestedBy,
                            Source,
                            onBackpressureRetry: (_, _, _) =>
                                Interlocked.Increment(
                                    ref replayTooManyRequestsRetryCount))
                        .ConfigureAwait(false);

                Assert.Equal(submittedRunCountPerCycle, replayProofs.Count);
                totalReplayProofCount =
                    checked(totalReplayProofCount + replayProofs.Count);

                var expectedExecutionIds =
                    replayProofs
                        .Select(proof => proof.ExecutionId)
                        .ToHashSet(StringComparer.Ordinal);

                Assert.Equal(
                    submittedRunCountPerCycle,
                    expectedExecutionIds.Count);

                var cycleLedgerToUtc = DateTimeOffset.UtcNow.AddSeconds(5);
                var executionLedgerEntries =
                    await QueryExecutionLedgerAsync(
                            mcp,
                            expectedExecutionIds,
                            cycleLedgerFromUtc,
                            cycleLedgerToUtc,
                            tenant.TenantId,
                            onBackpressureRetry: (_, _, _) =>
                                Interlocked.Increment(
                                    ref replayTooManyRequestsRetryCount))
                        .ConfigureAwait(false);
                var controlPlaneLedgerEntries =
                    await QueryControlPlaneRunLedgerAsync(
                            mcp,
                            admission.SharedRunIds,
                            cycleLedgerFromUtc,
                            cycleLedgerToUtc,
                            tenant.TenantId,
                            onBackpressureRetry: (_, _, _) =>
                                Interlocked.Increment(
                                    ref replayTooManyRequestsRetryCount))
                        .ConfigureAwait(false);

                var childRecoveryRuntimeInstanceIds =
                    childRuntimeFailureTarget is null
                        ? Array.Empty<string>()
                        : childRuntimeFailureTarget
                            .InitialHostRuntimeInstanceIds
                            .Concat(
                                childRuntimeFailureTarget.Host.RuntimeInstanceIds)
                            .ToArray();
                var parentRecoveryRuntimeInstanceIds =
                    recoveryProof is null
                        ? Array.Empty<string>()
                        : recoveryProof.FailedRuntimeInstanceIds
                            .Concat(
                                recoveryProof.ReplacementRuntimeInstanceIds)
                            .ToArray();
                var recoveryRuntimeInstanceIds =
                    childRecoveryRuntimeInstanceIds
                        .Concat(parentRecoveryRuntimeInstanceIds)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                var ledgerRuntimeInstanceIds =
                    completedRuns
                        .Select(run => run.AssignedRuntimeInstanceId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>()
                        .Concat(recoveryRuntimeInstanceIds)
                        .ToHashSet(StringComparer.Ordinal);
                var runtimeLifecycleLedgerEntries =
                    await QueryRuntimeLifecycleLedgerAsync(
                            mcp,
                            ledgerRuntimeInstanceIds,
                            cycleLedgerFromUtc,
                            cycleLedgerToUtc,
                            tenant.TenantId,
                            onBackpressureRetry: (_, _, _) =>
                                Interlocked.Increment(
                                    ref replayTooManyRequestsRetryCount))
                        .ConfigureAwait(false);
                var combinedLedgerEntries =
                    executionLedgerEntries
                        .Concat(controlPlaneLedgerEntries)
                        .Concat(runtimeLifecycleLedgerEntries)
                        .DistinctBy(entry => entry.EntryId)
                        .OrderBy(entry => entry.TimestampUtc)
                        .ThenBy(entry => entry.Sequence)
                        .ToArray();

                if (childRuntimeRecoveryProof is null &&
                    recoveryProof is null)
                {
                    Assert.DoesNotContain(
                        combinedLedgerEntries,
                        entry => entry.EventType.StartsWith(
                            "control.recovery.",
                            StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    Assert.Contains(
                        combinedLedgerEntries,
                        entry => entry.EventType.StartsWith(
                            "control.recovery.",
                            StringComparison.OrdinalIgnoreCase));
                }

                var childRecoveredExecutionIds =
                    childRuntimeRecoveryProof?.RecoveredWorks
                        .Select(
                            work =>
                                work.RecoveredExecutionId ??
                                work.Original.ExecutionId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>() ??
                    Array.Empty<string>();
                var childRecoveredSharedRunIds =
                    childRuntimeRecoveryProof?.RecoveredWorks
                        .Select(work => work.Original.SharedRunId) ??
                    Array.Empty<string>();
                var recoveredExecutionIds =
                    childRecoveredExecutionIds
                        .Concat(
                            recoveryProof?.RecoveredExecutionIds ??
                            new HashSet<string>(StringComparer.Ordinal))
                        .ToHashSet(StringComparer.Ordinal);
                var recoveredSharedRunIds =
                    childRecoveredSharedRunIds
                        .Concat(
                            recoveryProof?.RecoveredSharedRunIds ??
                            new HashSet<string>(StringComparer.Ordinal))
                        .ToHashSet(StringComparer.Ordinal);

                var stepLedgerProof =
                    RuntimePoolProductionCycleExecutor
                        .AssertLogicalStepCompletionEvidence(
                            executionLedgerEntries,
                            expectedExecutionIds,
                            recoveredExecutionIds,
                            StepCount,
                            $"{this.profile.LogPrefix} cycle {cycleNumber} logical step ledger proof");

                var dispatchLedgerProof =
                    RuntimePoolProductionCycleExecutor
                        .AssertDurableDispatchEvidence(
                            admission.SharedRunIds,
                            recoveredSharedRunIds,
                            controlPlaneLedgerEntries,
                            $"{this.profile.LogPrefix} cycle {cycleNumber} durable dispatch ledger proof");

                Assert.Equal(
                    logicalStepCountPerCycle,
                    stepLedgerProof.DistinctLogicalStepCompletedCount);
                Assert.Equal(
                    submittedRunCountPerCycle,
                    dispatchLedgerProof
                        .DurableDispatchProvenSharedRunIds
                        .Count);

                if (childRuntimeRecoveryProof is null &&
                    recoveryProof is null)
                {
                    Assert.Equal(
                        logicalStepCountPerCycle,
                        stepLedgerProof.RawStepCompletedEntryCount);
                    Assert.Equal(0, stepLedgerProof.DuplicateStepCompletedEntryCount);
                }
                else if (recoveryProof is not null)
                {
                    await AssertRecoveryForensicsAsync(
                            forensicsQueryService,
                            recoveryProof,
                            TimeSpan.FromMinutes(2))
                        .ConfigureAwait(false);
                }

                totalReplayTooManyRequestsRetryCount =
                    checked(
                        totalReplayTooManyRequestsRetryCount +
                        replayTooManyRequestsRetryCount);
                totalExecutionLedgerEntryCount =
                    checked(
                        totalExecutionLedgerEntryCount +
                        executionLedgerEntries.Count);
                totalControlPlaneLedgerEntryCount =
                    checked(
                        totalControlPlaneLedgerEntryCount +
                        controlPlaneLedgerEntries.Count);
                totalRuntimeLifecycleLedgerEntryCount =
                    checked(
                        totalRuntimeLifecycleLedgerEntryCount +
                        runtimeLifecycleLedgerEntries.Count);
                totalRawStepCompletedLedgerEntryCount =
                    checked(
                        totalRawStepCompletedLedgerEntryCount +
                        stepLedgerProof.RawStepCompletedEntryCount);
                totalDistinctLogicalStepCompletedLedgerCount =
                    checked(
                        totalDistinctLogicalStepCompletedLedgerCount +
                        stepLedgerProof.DistinctLogicalStepCompletedCount);
                totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount =
                    checked(
                        totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount +
                        stepLedgerProof.DuplicateStepCompletedEntryCount);
                totalRecoveryForensicsCount =
                    checked(
                        totalRecoveryForensicsCount +
                        (recoveryProof?.RecoveryForensicsIds.Count ?? 0));

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} MCP REPLAY PROOF] Cycle='{cycleNumber}', TenantId='{tenant.TenantId}', ReplayProofCount='{replayProofs.Count}', ExecutionIds='{string.Join(",", expectedExecutionIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                ProductionTenantLedgerSummaryOutput.Write(
                    this.output,
                    executionCycleCount == 1
                        ? "TENANT-SCOPED LEDGER SUMMARY"
                        : $"TENANT-SCOPED LEDGER SUMMARY - CYCLE {cycleNumber}",
                    new[]
                    {
                        new ProductionTenantLedgerSummary(
                            tenant.TenantId,
                            completedRuns
                                .Select(run => run.AssignedRuntimeInstanceId)
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Cast<string>()
                                .Distinct(StringComparer.Ordinal)
                                .ToArray(),
                            expectedExecutionIds,
                            combinedLedgerEntries)
                    },
                    maxLedgerEntriesPerTenant: 50,
                    maxEventTypeRowsPerTenant: 30,
                    maxLedgerEntriesPerExecution: 25);

                var childRecoveryForensicsIds =
                    childRuntimeRecoveryForensics
                        .Select(record => record.ForensicsId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Cast<string>();
                var parentRecoveryForensicsIds =
                    recoveryProof?.RecoveryForensicsIds ??
                    new HashSet<string>(StringComparer.Ordinal);
                var recoveryForensicsIds =
                    childRecoveryForensicsIds
                        .Concat(parentRecoveryForensicsIds)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                Assert.Equal(
                    recoveredSharedRunIds.Count,
                    recoveryForensicsIds.Length);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} RECOVERY FORENSICS PROOF] Cycle='{cycleNumber}', ChildRuntimeForensicsCount='{childRuntimeRecoveryForensics.Count}', ParentFailureId='{recoveryProof?.FailureId ?? string.Empty}', ParentForensicsCount='{recoveryProof?.RecoveryForensicsIds.Count ?? 0}', TotalRecoveryForensicsCount='{recoveryForensicsIds.Length}', RecoveryForensicsIds='{string.Join(",", recoveryForensicsIds)}'.");
                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} MCP REPLAY BACKPRESSURE] Cycle='{cycleNumber}', DedicatedRbacContext='true', MaximumAttemptCount='{MaximumAdmissionAttemptCount}', TooManyRequestsRetryCount='{replayTooManyRequestsRetryCount}'.");

                replayAndLedgerStopwatch.Stop();

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='Validate MCP replay, ledger, trace, forensics, and lifecycle evidence', Duration='{replayAndLedgerStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                if (cycleNumber == executionCycleCount)
                {
                    this.WritePhase(
                        phaseNumber: 6,
                        cycleNumber: null,
                        title:
                            "FINAL TOPOLOGY PERFORMANCE AND SAFETY PROOF",
                        passTarget:
                            "Print complete parent ProcessHost/runtime membership, run placement, timing, throughput, datastore, replay, ledger, and safety evidence before deterministic cleanup.");

                    finalProofStopwatch = Stopwatch.StartNew();
                }

                WriteTopology(
                    $"CYCLE {cycleNumber} FINAL",
                    cluster,
                    cycleFinalTopology,
                    this.output,
                    this.profile.LogPrefix);

                var cycleRunPlacements =
                    CreateRuntimeRunPlacements(
                        tenant,
                        completedRuns,
                        childRuntimeFailureTarget,
                        childRuntimeRecoveryForensics,
                        failureTarget,
                        recoveryProof,
                        injectChildRuntimeFailure ||
                        injectParentHostFailure);

                allRunPlacements.AddRange(cycleRunPlacements);

                previousFinalHostIds = cycleFinalHostIds;
                previousFinalParentProcessIds =
                    cycleFinalParentProcessIds;
                previousFinalRuntimeInstanceIds =
                    cycleFinalRuntimeInstanceIds;

                cycleStopwatch.Stop();

                var cycleTiming =
                    new ProcessHostPoolProductionCycleTiming(
                        cycleNumber,
                        submissionStopwatch.Elapsed,
                        drainStopwatch.Elapsed,
                        safetyStopwatch.Elapsed,
                        replayAndLedgerStopwatch.Elapsed,
                        cycleStopwatch.Elapsed);

                cycleTimings.Add(cycleTiming);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} STEP LEDGER PROOF] Cycle='{cycleNumber}', ExpectedLogicalStepCount='{logicalStepCountPerCycle}', DistinctLogicalStepCompletedCount='{stepLedgerProof.DistinctLogicalStepCompletedCount}', RawStepCompletedEntryCount='{stepLedgerProof.RawStepCompletedEntryCount}', RecoveryCoveredDuplicateEntryCount='{stepLedgerProof.DuplicateStepCompletedEntryCount}', DuplicateEvidenceExecutionIds='{string.Join(",", stepLedgerProof.DuplicateEvidenceExecutionIds.OrderBy(value => value, StringComparer.Ordinal))}'.");
                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} DISPATCH LEDGER PROOF] Cycle='{cycleNumber}', SubmittedRunCount='{submittedRunCountPerCycle}', InitialDispatchSucceededCount='{dispatchLedgerProof.InitialDispatchSucceededSharedRunIds.Count}', RecoveryCoveredMissingInitialDispatchCount='{dispatchLedgerProof.RecoveryCoveredSharedRunIds.Count}', DurableDispatchProvenCount='{dispatchLedgerProof.DurableDispatchProvenSharedRunIds.Count}', RecoveryCoveredSharedRunIds='{string.Join(",", dispatchLedgerProof.RecoveryCoveredSharedRunIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

                this.WriteCycleProductionSummary(
                    cycleNumber,
                    maximumProcessHostCount,
                    runtimeCountPerHost,
                    submissionIterationCount,
                    totalRuntimeCount,
                    logicalStepCountPerCycle,
                    admission,
                    completedRuns,
                    finalStatuses,
                    replayProofs.Count,
                    executionLedgerEntries.Count,
                    controlPlaneLedgerEntries.Count,
                    runtimeLifecycleLedgerEntries.Count,
                    stepLedgerProof,
                    dispatchLedgerProof,
                    childRuntimeFailureTarget,
                    childRuntimeRecoveryProof,
                    childRuntimeRecoveryForensics,
                    recoveryProof,
                    cycleFinalRuntimeInstanceIds.Count,
                    replayTooManyRequestsRetryCount,
                    cycleTiming);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} CYCLE COMPLETE] Cycle='{cycleNumber}', SubmittedRunCount='{admission.SharedRunIds.Count}', CompletedRunCount='{finalStatuses.Count}', ReplayProofCount='{replayProofs.Count}', ProcessHostCount='{cluster.Hosts.Count}', RuntimeCount='{cycleFinalRuntimeInstanceIds.Count}', ChildRuntimeCrashCount='{(childRuntimeRecoveryProof is null ? 0 : 1)}', ParentHostCrashCount='{(recoveryProof is null ? 0 : 1)}', RecoveredRunCount='{recoveredSharedRunIds.Count}', Duration='{cycleStopwatch.Elapsed}'.");
            }

            Assert.Equal(totalSubmittedRunCount, allRunPlacements.Count);
            Assert.Equal(
                totalSubmittedRunCount,
                allRunPlacements
                    .Select(placement => placement.SharedRunId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var topologySummary =
                await ProductionRuntimeTopologySummaryOutput
                    .CreateAsync(
                        registry,
                        controlPlaneId,
                        AiRuntimeHostCreationMode.Process,
                        allRunPlacements,
                        historicalRuntimeSnapshots:
                            historicalRuntimeSnapshots.Values.ToArray(),
                        lifecycleJournal: lifecycleJournal,
                        tenantRoles:
                            new Dictionary<string, string>(
                                StringComparer.Ordinal)
                            {
                                [tenant.TenantId] = (injectChildRuntimeFailure || injectParentHostFailure)
                                    ? "Impacted"
                                    : "Capacity"
                            })
                    .ConfigureAwait(false);

            if (!topologySummary.Contains(
                    "TopologySource='RuntimeLifecycleJournal'",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The durable runtime lifecycle journal was enabled, but the ProcessHostPool topology summary did not use it. ControlPlaneId='",
                        controlPlaneId,
                        "'."));
            }

            this.output.WriteLine(
                CreateProcessHostPoolTopologySummary(
                    topologySummary,
                    cluster,
                    historicalRuntimeSnapshots.Values.ToArray()));

            scenarioStopwatch.Stop();

            Assert.Equal(
                checked(submittedRunCountPerCycle * executionCycleCount),
                totalSubmittedRunCount);
            Assert.Equal(totalSubmittedRunCount, totalCompletedRunCount);
            Assert.Equal(totalSubmittedRunCount, totalReplayProofCount);
            var expectedRecoveredRunCountPerCycle =
                (injectChildRuntimeFailure ? 1 : 0) +
                (injectParentHostFailure ? runtimeCountPerHost : 0);

            Assert.Equal(
                checked(
                    expectedRecoveredRunCountPerCycle *
                    executionCycleCount),
                totalRecoveredRunCount);
            Assert.Equal(
                injectChildRuntimeFailure
                    ? executionCycleCount
                    : 0,
                totalChildRuntimeCrashCount);
            Assert.Equal(
                injectParentHostFailure
                    ? executionCycleCount
                    : 0,
                totalParentHostCrashCount);
            Assert.Equal(
                totalRecoveredRunCount,
                totalRecoveryForensicsCount);

            this.WriteFinalProductionSummary(
                maximumProcessHostCount,
                runtimeCountPerHost,
                submissionIterationCount,
                executionCycleCount,
                totalRuntimeCount,
                totalSubmittedRunCount,
                totalCompletedRunCount,
                totalReplayProofCount,
                totalRecoveredRunCount,
                totalChildRuntimeCrashCount,
                totalParentHostCrashCount,
                totalAdmissionTooManyRequestsRetryCount,
                totalReplayTooManyRequestsRetryCount,
                totalExecutionLedgerEntryCount,
                totalControlPlaneLedgerEntryCount,
                totalRuntimeLifecycleLedgerEntryCount,
                totalRawStepCompletedLedgerEntryCount,
                totalDistinctLogicalStepCompletedLedgerCount,
                totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount,
                totalRecoveryForensicsCount,
                cluster.Hosts.Count(host => host.IsRunning),
                scenarioStopwatch.Elapsed,
                injectChildRuntimeFailure,
                injectParentHostFailure,
                waitForExternalParentHostFailure);

            if (finalProofStopwatch is null)
            {
                throw new InvalidOperationException(
                    "The final ProcessHostPool proof phase was not started.");
            }

            finalProofStopwatch.Stop();
            finalProofDuration = finalProofStopwatch.Elapsed;

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} TIMING] Phase='Produce final topology, run placement, performance, datastore, and safety proof', Duration='{finalProofDuration}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");

            this.WriteTimingSummary(
                setupDuration,
                cycleTimings,
                finalProofDuration,
                scenarioStopwatch.Elapsed,
                injectChildRuntimeFailure,
                injectParentHostFailure);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} FINAL PRODUCTION RESULT] ExecutionCycleCount='{executionCycleCount}', ProcessHostCount='{maximumProcessHostCount}', RuntimeCountPerHost='{runtimeCountPerHost}', TotalRuntimeCount='{totalRuntimeCount}', SubmissionIterationCountPerCycle='{submissionIterationCount}', TotalSubmittedRunCount='{totalSubmittedRunCount}', TotalCompletedRunCount='{totalCompletedRunCount}', TotalLogicalStepCount='{checked(totalSubmittedRunCount * StepCount)}', TotalReplayProofCount='{totalReplayProofCount}', ChildRuntimeCrashCount='{totalChildRuntimeCrashCount}', ParentHostCrashCount='{totalParentHostCrashCount}', ParentHostFailureTrigger='{(injectParentHostFailure ? (waitForExternalParentHostFailure ? "external-manual" : "automatic") : "none")}', RecoveredRunCount='{totalRecoveredRunCount}', FinalParentProcessCountAlive='{cluster.Hosts.Count(host => host.IsRunning)}', DurationBeforeCleanup='{scenarioStopwatch.Elapsed}', CleanupPolicy='after-final-cycle-only'.");
        }

        private static void CaptureRuntimeTopologyHistory(
            IDictionary<string, AiRuntimeInstanceSnapshot> history,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> snapshots)
        {
            ArgumentNullException.ThrowIfNull(history);
            ArgumentNullException.ThrowIfNull(snapshots);

            foreach (var snapshot in snapshots)
            {
                if (!history.TryGetValue(
                        snapshot.RuntimeInstanceId,
                        out var existing) ||
                    snapshot.SnapshotAtUtc >= existing.SnapshotAtUtc)
                {
                    history[snapshot.RuntimeInstanceId] = snapshot;
                }
            }
        }

        private static string CreateProcessHostPoolTopologySummary(
            string providerNeutralSummary,
            ProcessHostPoolProductionCluster cluster,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> observedRuntimeSnapshots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                providerNeutralSummary);
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(observedRuntimeSnapshots);

            var activeHosts =
                cluster.Hosts
                    .Where(host => host.IsRunning)
                    .OrderBy(host => host.Ordinal)
                    .ToArray();
            var activeParentHostIds =
                activeHosts
                    .Select(host => host.HostId)
                    .ToHashSet(StringComparer.Ordinal);
            var activeRuntimeInstanceIds =
                activeHosts
                    .SelectMany(host => host.RuntimeInstanceIds)
                    .ToHashSet(StringComparer.Ordinal);
            var historicalRuntimeSnapshots =
                observedRuntimeSnapshots
                    .Where(snapshot =>
                        !activeRuntimeInstanceIds.Contains(
                            snapshot.RuntimeInstanceId))
                    .OrderBy(snapshot => snapshot.RegisteredAtUtc)
                    .ThenBy(
                        snapshot => snapshot.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray();
            var historicalParentGroups =
                historicalRuntimeSnapshots
                    .Where(snapshot =>
                        !string.IsNullOrWhiteSpace(snapshot.HostId) &&
                        !activeParentHostIds.Contains(snapshot.HostId!))
                    .GroupBy(snapshot => snapshot.HostId!, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToArray();
            var observedRuntimeInstanceCount =
                activeRuntimeInstanceIds
                    .Concat(
                        historicalRuntimeSnapshots.Select(
                            snapshot => snapshot.RuntimeInstanceId))
                    .Distinct(StringComparer.Ordinal)
                    .Count();

            var lines =
                providerNeutralSummary
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n');
            var specialized = new List<string>(lines.Length + 64);
            var countersWritten = false;

            foreach (var line in lines)
            {
                if (string.Equals(
                        line,
                        "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY",
                        StringComparison.Ordinal))
                {
                    specialized.Add(
                        "# PROCESS HOST POOL TOPOLOGY AND RUN PLACEMENT SUMMARY");
                    continue;
                }

                if (line.StartsWith(
                        "Scope=",
                        StringComparison.Ordinal))
                {
                    specialized.Add(
                        "Scope='Authoritative ProcessHostPool parent/runtime counters from the live cluster plus durable failed-membership history; detailed lifecycle entities and run placement follow.'");
                    continue;
                }

                if (line.StartsWith(
                        "ObservedPhysicalHostCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "ActivePhysicalHostCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "ObservedKubernetesPodCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "ActiveKubernetesPodCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "HistoricalOnlyPhysicalHostCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "HistoricalOnlyKubernetesPodCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "DeletedPodCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "HistoricalRuntimeCount=",
                        StringComparison.Ordinal) ||
                    line.StartsWith(
                        "ObservedRuntimeInstanceCount=",
                        StringComparison.Ordinal))
                {
                    if (!countersWritten)
                    {
                        specialized.Add(
                            $"ObservedParentProcessHostCount='{checked(activeHosts.Length + historicalParentGroups.Length)}'");
                        specialized.Add(
                            $"ActiveParentProcessHostCount='{activeHosts.Length}'");
                        specialized.Add(
                            $"HistoricalParentProcessHostCount='{historicalParentGroups.Length}'");
                        specialized.Add(
                            $"ActiveRuntimeInstanceCount='{activeRuntimeInstanceIds.Count}'");
                        specialized.Add(
                            $"HistoricalRuntimeInstanceCount='{historicalRuntimeSnapshots.Length}'");
                        specialized.Add(
                            $"ObservedRuntimeInstanceCount='{observedRuntimeInstanceCount}'");
                        specialized.Add(
                            "ControlPlaneRuntimeExcluded='true'");
                        countersWritten = true;
                    }

                    continue;
                }

                if (string.Equals(
                        line,
                        "Physical hosts and runtime membership:",
                        StringComparison.Ordinal))
                {
                    specialized.Add(
                        "Parent Process Hosts and runtime membership:");

                    foreach (var host in activeHosts)
                    {
                        specialized.Add(
                            string.Concat(
                                "  Parent[",
                                host.Ordinal.ToString(
                                    "D2",
                                    CultureInfo.InvariantCulture),
                                "] Lifecycle='Active', ParentProcessId='",
                                host.ProcessId.ToString(
                                    CultureInfo.InvariantCulture),
                                "', HostId='",
                                host.HostId,
                                "', PoolId='",
                                host.PoolId,
                                "', StableTransportEndpoint='",
                                host.StableTransportEndpoint,
                                "', RuntimeCount='",
                                host.RuntimeInstanceIds.Count.ToString(
                                    CultureInfo.InvariantCulture),
                                "'."));

                        var orderedRuntimeIds =
                            host.RuntimeInstanceIds
                                .OrderBy(
                                    runtimeInstanceId => runtimeInstanceId,
                                    StringComparer.Ordinal)
                                .ToArray();

                        for (var runtimeIndex = 0;
                             runtimeIndex < orderedRuntimeIds.Length;
                             runtimeIndex++)
                        {
                            specialized.Add(
                                string.Concat(
                                    "    Runtime[",
                                    (runtimeIndex + 1).ToString(
                                        "D2",
                                        CultureInfo.InvariantCulture),
                                    "] RuntimeInstanceId='",
                                    orderedRuntimeIds[runtimeIndex],
                                    "', SnapshotSource='LiveProcessHostPool'."));
                        }
                    }

                    specialized.Add(
                        "Historical parent Process Hosts and runtime membership:");

                    if (historicalParentGroups.Length == 0)
                    {
                        specialized.Add("  (none)");
                    }
                    else
                    {
                        for (var hostIndex = 0;
                             hostIndex < historicalParentGroups.Length;
                             hostIndex++)
                        {
                            var historicalHost = historicalParentGroups[hostIndex];
                            var historicalRuntimes =
                                historicalHost
                                    .OrderBy(
                                        snapshot => snapshot.RuntimeInstanceId,
                                        StringComparer.Ordinal)
                                    .ToArray();

                            specialized.Add(
                                string.Concat(
                                    "  HistoricalParent[",
                                    (hostIndex + 1).ToString(
                                        "D2",
                                        CultureInfo.InvariantCulture),
                                    "] Lifecycle='HistoricalOnly', HostId='",
                                    historicalHost.Key,
                                    "', PoolId='",
                                    historicalRuntimes
                                        .Select(snapshot => snapshot.PoolId)
                                        .FirstOrDefault(value =>
                                            !string.IsNullOrWhiteSpace(value)) ??
                                    string.Empty,
                                    "', RuntimeCount='",
                                    historicalRuntimes.Length.ToString(
                                        CultureInfo.InvariantCulture),
                                    "'."));

                            for (var runtimeIndex = 0;
                                 runtimeIndex < historicalRuntimes.Length;
                                 runtimeIndex++)
                            {
                                specialized.Add(
                                    string.Concat(
                                        "    Runtime[",
                                        (runtimeIndex + 1).ToString(
                                            "D2",
                                            CultureInfo.InvariantCulture),
                                        "] RuntimeInstanceId='",
                                        historicalRuntimes[runtimeIndex]
                                            .RuntimeInstanceId,
                                        "', SnapshotSource='DurableHistory'."));
                            }
                        }
                    }

                    specialized.Add(string.Empty);
                    specialized.Add(
                        "Durable lifecycle entities and runtime membership:");
                    specialized.Add(
                        "EntityProjectionNote='Runtime lifecycle events without a parent HostId remain visible below as RuntimeHost entities, but they are excluded from the authoritative parent ProcessHost counters above. The mcp-control-plane runtime is also excluded.'");
                    continue;
                }

                specialized.Add(line);
            }

            return string.Join(Environment.NewLine, specialized);
        }

        private static IReadOnlyList<ProductionRuntimeRunPlacement>
            CreateRuntimeRunPlacements(
                ProductionTenantScenarioDefinition tenant,
                IReadOnlyCollection<AiSharedRunRecord> completedRuns,
                ProcessHostPoolChildRuntimeFailureTarget?
                    childRuntimeFailureTarget,
                IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel>
                    childRuntimeRecoveryForensics,
                ProcessHostPoolProductionFailureTarget? failureTarget,
                ProcessHostPoolProductionRecoveryProof? recoveryProof,
                bool failureInjected)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(completedRuns);
            ArgumentNullException.ThrowIfNull(childRuntimeRecoveryForensics);

            var initialActiveRuns =
                failureTarget?.ActiveRuns.ToDictionary(
                    run => run.SharedRunId,
                    StringComparer.Ordinal) ??
                new Dictionary<string, ProcessHostPoolProductionActiveRun>(
                    StringComparer.Ordinal);

            if (childRuntimeFailureTarget is not null)
            {
                initialActiveRuns[
                    childRuntimeFailureTarget.ActiveRun.SharedRunId] =
                    childRuntimeFailureTarget.ActiveRun;
            }

            var childForensicsBySharedRunId =
                childRuntimeRecoveryForensics
                    .Where(
                        record =>
                            !string.IsNullOrWhiteSpace(record.SharedRunId))
                    .ToDictionary(
                        record => record.SharedRunId!,
                        StringComparer.Ordinal);

            return completedRuns
                .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                .Select(
                    run =>
                    {
                        initialActiveRuns.TryGetValue(
                            run.SharedRunId,
                            out var initialActiveRun);
                        childForensicsBySharedRunId.TryGetValue(
                            run.SharedRunId,
                            out var childForensics);

                        string? parentForensicsId = null;

                        if (initialActiveRun is not null &&
                            recoveryProof is not null)
                        {
                            var expectedForensicsId =
                                string.Join(
                                    ":",
                                    "runtime-recovery",
                                    initialActiveRun.ExecutionId,
                                    initialActiveRun.SharedRunId,
                                    initialActiveRun.LocalRunId);

                            if (recoveryProof.RecoveryForensicsIds.Contains(
                                    expectedForensicsId))
                            {
                                parentForensicsId = expectedForensicsId;
                            }
                        }

                        return new ProductionRuntimeRunPlacement
                        {
                            TenantId = tenant.TenantId,
                            TenantRole = failureInjected
                                ? "Impacted"
                                : "Capacity",
                            SharedRunId = run.SharedRunId,
                            WorkKind = initialActiveRun is null
                                ? "CompletedExecution"
                                : "InFlightExecution",
                            PipelineName =
                                run.PipelineKey ?? run.RunRequest.PipelineName,
                            InitialRuntimeInstanceId =
                                initialActiveRun?.RuntimeInstanceId ??
                                run.AssignedRuntimeInstanceId,
                            InitialLocalRunId =
                                initialActiveRun?.LocalRunId ?? run.LocalRunId,
                            InitialExecutionId =
                                initialActiveRun?.ExecutionId ?? run.ExecutionId,
                            CurrentRuntimeInstanceId =
                                run.AssignedRuntimeInstanceId,
                            CurrentLocalRunId = run.LocalRunId,
                            CurrentExecutionId = run.ExecutionId,
                            RuntimeFailureIncidentId =
                                childForensics?.RuntimeFailureIncidentId ??
                                (initialActiveRun is null
                                    ? null
                                    : recoveryProof?.FailureId),
                            ForensicsId =
                                childForensics?.ForensicsId ??
                                parentForensicsId
                        };
                    })
                .ToArray();
        }

        private void WriteCycleProductionSummary(
            int cycleNumber,
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int totalRuntimeCount,
            int logicalStepCount,
            RuntimePoolProductionCycleAdmissionProof admission,
            IReadOnlyCollection<AiSharedRunRecord> completedRuns,
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> finalStatuses,
            int replayProofCount,
            int executionLedgerEntryCount,
            int controlPlaneLedgerEntryCount,
            int runtimeLifecycleLedgerEntryCount,
            RuntimePoolProductionStepLedgerProof stepLedgerProof,
            RuntimePoolProductionDispatchLedgerProof dispatchLedgerProof,
            ProcessHostPoolChildRuntimeFailureTarget?
                childRuntimeFailureTarget,
            RealRuntimeCrashFailedRuntimeRecoveryProof?
                childRuntimeRecoveryProof,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel>
                childRuntimeRecoveryForensics,
            ProcessHostPoolProductionRecoveryProof? recoveryProof,
            int finalRuntimeInstanceCount,
            int replayTooManyRequestsRetryCount,
            ProcessHostPoolProductionCycleTiming timing)
        {
            var distinctAssignedRuntimeCount =
                completedRuns
                    .Select(run => run.AssignedRuntimeInstanceId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
            var failedRunCount =
                finalStatuses.Count(
                    status =>
                        !status.Success ||
                        !string.Equals(
                            status.RunState?.Status,
                            "completed",
                            StringComparison.OrdinalIgnoreCase));
            var executionsPerSecond =
                timing.TotalDuration.TotalSeconds <= 0
                    ? 0
                    : completedRuns.Count /
                      timing.TotalDuration.TotalSeconds;
            var logicalStepsPerSecond =
                timing.TotalDuration.TotalSeconds <= 0
                    ? 0
                    : logicalStepCount /
                      timing.TotalDuration.TotalSeconds;
            var childRecoveredRunCount =
                childRuntimeRecoveryProof?.RecoveredWorks.Count ?? 0;
            var parentRecoveredRunCount =
                recoveryProof?.RecoveredSharedRunIds.Count ?? 0;
            var totalCycleRecoveredRunCount =
                checked(childRecoveredRunCount + parentRecoveredRunCount);
            var totalCycleRecoveryForensicsCount =
                checked(
                    childRuntimeRecoveryForensics.Count +
                    (recoveryProof?.RecoveryForensicsIds.Count ?? 0));
            var recoveryInjected =
                childRuntimeRecoveryProof is not null ||
                recoveryProof is not null;

            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} CYCLE {cycleNumber} MACHINE LIMIT AND SAFETY SUMMARY");
            this.output.WriteLine(
                $"MaximumConfiguredProcessHostCount='{maximumProcessHostCount}'");
            this.output.WriteLine(
                $"RuntimeCountPerHost='{runtimeCountPerHost}'");
            this.output.WriteLine(
                $"MaximumRuntimeCapacity='{totalRuntimeCount}'");
            this.output.WriteLine(
                $"SubmissionIterationCount='{submissionIterationCount}'");
            this.output.WriteLine(
                $"RunsPerIteration='{totalRuntimeCount}'");
            this.output.WriteLine(
                $"SubmittedRunCount='{admission.SharedRunIds.Count}'");
            this.output.WriteLine(
                $"CompletedRunCount='{completedRuns.Count}'");
            this.output.WriteLine(
                $"LogicalStepCount='{logicalStepCount}'");
            this.output.WriteLine(
                $"MaximumObservedProcessHostCount='{maximumProcessHostCount}'");
            this.output.WriteLine(
                $"MaximumObservedRuntimeCount='{finalRuntimeInstanceCount}'");
            this.output.WriteLine(
                $"DistinctAssignedRuntimeCount='{distinctAssignedRuntimeCount}'");
            this.output.WriteLine(
                $"ConfiguredFullCapacityReached='{(finalRuntimeInstanceCount == totalRuntimeCount).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"ObservedAllRuntimeCapacityUsed='{(distinctAssignedRuntimeCount == totalRuntimeCount).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"AdmissionTooManyRequestsRetryCount='{admission.TooManyRequestsRetryCount}'");
            this.output.WriteLine(
                $"ReplayAndLedgerTooManyRequestsRetryCount='{replayTooManyRequestsRetryCount}'");
            this.output.WriteLine(
                $"DuplicateDispatchCount='{admission.SharedRunIds.Count - dispatchLedgerProof.DurableDispatchProvenSharedRunIds.Count}'");
            this.output.WriteLine(
                $"LostRunCount='{admission.SharedRunIds.Count - completedRuns.Count}'");
            this.output.WriteLine($"FailedRunCount='{failedRunCount}'");
            this.output.WriteLine(
                $"ChildRuntimeFailureInjected='{(childRuntimeRecoveryProof is not null).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"FailedChildRuntimeInstanceId='{childRuntimeFailureTarget?.Runtime.RuntimeInstanceId ?? string.Empty}'");
            this.output.WriteLine(
                $"ChildRuntimeParentHostId='{childRuntimeFailureTarget?.Host.HostId ?? string.Empty}'");
            this.output.WriteLine(
                $"ChildRuntimeParentProcessId='{(childRuntimeFailureTarget is null ? string.Empty : childRuntimeFailureTarget.Host.ProcessId.ToString(CultureInfo.InvariantCulture))}'");
            this.output.WriteLine(
                $"ChildRuntimeSiblingCount='{childRuntimeFailureTarget?.SiblingRuntimeInstanceIds.Count ?? 0}'");
            this.output.WriteLine(
                $"ChildRuntimeRecoveredSharedRunCount='{childRecoveredRunCount}'");
            this.output.WriteLine(
                $"ChildRuntimeRecoveryForensicsCount='{childRuntimeRecoveryForensics.Count}'");
            this.output.WriteLine(
                $"ParentHostFailureInjected='{(recoveryProof is not null).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"ParentHostFailureId='{recoveryProof?.FailureId ?? string.Empty}'");
            this.output.WriteLine(
                $"FailedParentProcessId='{(recoveryProof is null ? string.Empty : recoveryProof.FailedHost.ProcessId.ToString(CultureInfo.InvariantCulture))}'");
            this.output.WriteLine(
                $"ReplacementParentProcessId='{(recoveryProof is null ? string.Empty : recoveryProof.ReplacementHost.ProcessId.ToString(CultureInfo.InvariantCulture))}'");
            this.output.WriteLine(
                $"FailedHostId='{recoveryProof?.FailedHost.HostId ?? string.Empty}'");
            this.output.WriteLine(
                $"ReplacementHostId='{recoveryProof?.ReplacementHost.HostId ?? string.Empty}'");
            this.output.WriteLine(
                $"FailedRuntimeCount='{recoveryProof?.FailedRuntimeInstanceIds.Count ?? 0}'");
            this.output.WriteLine(
                $"ReplacementRuntimeCount='{recoveryProof?.ReplacementRuntimeInstanceIds.Count ?? 0}'");
            this.output.WriteLine(
                $"ParentRecoveredSharedRunCount='{parentRecoveredRunCount}'");
            this.output.WriteLine(
                $"ParentRecoveryForensicsCount='{recoveryProof?.RecoveryForensicsIds.Count ?? 0}'");
            this.output.WriteLine(
                $"TotalRecoveredSharedRunCount='{totalCycleRecoveredRunCount}'");
            this.output.WriteLine(
                $"TotalRecoveryForensicsCount='{totalCycleRecoveryForensicsCount}'");
            this.output.WriteLine(
                $"SubmissionDuration='{timing.SubmissionDuration}'");
            this.output.WriteLine(
                $"DrainDuration='{timing.DrainDuration}'");
            this.output.WriteLine(
                $"ReplayLedgerTraceDuration='{timing.ReplayLedgerTraceDuration}'");
            this.output.WriteLine(
                $"CycleTotalDuration='{timing.TotalDuration}'");
            this.output.WriteLine(
                $"ExecutionsPerSecond='{executionsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}'");
            this.output.WriteLine(
                $"LogicalStepsPerSecond='{logicalStepsPerSecond.ToString("F3", CultureInfo.InvariantCulture)}'");
            this.output.WriteLine($"ReplayProofCount='{replayProofCount}'");
            this.output.WriteLine(
                $"ExecutionLedgerEntryCount='{executionLedgerEntryCount}'");
            this.output.WriteLine(
                $"ControlPlaneLedgerEntryCount='{controlPlaneLedgerEntryCount}'");
            this.output.WriteLine(
                $"RuntimeLifecycleLedgerEntryCount='{runtimeLifecycleLedgerEntryCount}'");
            this.output.WriteLine(
                $"DispatchedSharedRunLedgerCount='{dispatchLedgerProof.DurableDispatchProvenSharedRunIds.Count}'");
            this.output.WriteLine(
                $"RawStepCompletedLedgerEntryCount='{stepLedgerProof.RawStepCompletedEntryCount}'");
            this.output.WriteLine(
                $"DistinctLogicalStepCompletedLedgerCount='{stepLedgerProof.DistinctLogicalStepCompletedCount}'");
            this.output.WriteLine(
                $"RecoveryCoveredDuplicateStepCompletedLedgerEntryCount='{stepLedgerProof.DuplicateStepCompletedEntryCount}'");
            this.output.WriteLine(string.Empty);
            this.output.WriteLine("Safety:");
            this.output.WriteLine("  QueueDrained='true'");
            this.output.WriteLine("  ExactDispatchValidated='true'");
            this.output.WriteLine("  DagCompletionValidated='true'");
            this.output.WriteLine("  ReplayValidated='true'");
            this.output.WriteLine("  LedgerValidated='true'");
            this.output.WriteLine("  LogicalStepLedgerIdentityValidated='true'");
            this.output.WriteLine(
                $"  DuplicateStepLedgerEvidenceOutsideRecoveryDetected='{(stepLedgerProof.DuplicateStepCompletedEntryCount > 0 && !recoveryInjected).ToString().ToLowerInvariant()}'");
            this.output.WriteLine("  TraceValidated='true'");
            this.output.WriteLine(
                $"  ExactChildRuntimeRecoveryValidated='{(childRuntimeRecoveryProof is null || (childRecoveredRunCount == 1 && childRuntimeRecoveryForensics.Count == childRecoveredRunCount)).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"  ChildRuntimeParentBoundarySurvived='{(childRuntimeFailureTarget is null || childRuntimeFailureTarget.Host.IsRunning).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"  ChildRuntimeSiblingIdentityPreserved='{(childRuntimeFailureTarget is null || childRuntimeFailureTarget.SiblingRuntimeInstanceIds.IsSubsetOf(childRuntimeFailureTarget.Host.RuntimeInstanceIds)).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"  ExactParentHostRecoveryValidated='{(recoveryProof is null || recoveryProof.RecoveredSharedRunIds.Count == runtimeCountPerHost).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"  RecoveryForensicsValidated='{(!recoveryInjected || totalCycleRecoveryForensicsCount == totalCycleRecoveredRunCount).ToString().ToLowerInvariant()}'");
            this.output.WriteLine("  DuplicateDispatchDetected='false'");
            this.output.WriteLine("  LostRunDetected='false'");
            this.output.WriteLine("  FailedRunDetected='false'");
            this.output.WriteLine("  ProcessHostCapacityExceeded='false'");
            this.output.WriteLine(
                $"  RuntimeCapacityExceeded='{(finalRuntimeInstanceCount > totalRuntimeCount).ToString().ToLowerInvariant()}'");
        }

        private void WriteFinalProductionSummary(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount,
            int totalRuntimeCount,
            int totalSubmittedRunCount,
            int totalCompletedRunCount,
            int totalReplayProofCount,
            int totalRecoveredRunCount,
            int totalChildRuntimeCrashCount,
            int totalParentHostCrashCount,
            int totalAdmissionTooManyRequestsRetryCount,
            int totalReplayTooManyRequestsRetryCount,
            int totalExecutionLedgerEntryCount,
            int totalControlPlaneLedgerEntryCount,
            int totalRuntimeLifecycleLedgerEntryCount,
            int totalRawStepCompletedLedgerEntryCount,
            int totalDistinctLogicalStepCompletedLedgerCount,
            int totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount,
            int totalRecoveryForensicsCount,
            int finalParentProcessCountAlive,
            TimeSpan scenarioDuration,
            bool injectChildRuntimeFailure,
            bool injectParentHostFailure,
            bool waitForExternalParentHostFailure)
        {
            var recoveryInjected =
                injectChildRuntimeFailure || injectParentHostFailure;

            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} PRODUCTION SUMMARY");
            this.output.WriteLine(
                $"ExecutionCycleCount='{executionCycleCount}'");
            this.output.WriteLine(
                $"MaximumConfiguredProcessHostCount='{maximumProcessHostCount}'");
            this.output.WriteLine(
                $"RuntimeCountPerHost='{runtimeCountPerHost}'");
            this.output.WriteLine(
                $"MaximumRuntimeCapacity='{totalRuntimeCount}'");
            this.output.WriteLine(
                $"SubmissionIterationCountPerCycle='{submissionIterationCount}'");
            this.output.WriteLine(
                $"TotalSubmittedRunCount='{totalSubmittedRunCount}'");
            this.output.WriteLine(
                $"TotalCompletedRunCount='{totalCompletedRunCount}'");
            this.output.WriteLine(
                $"TotalLogicalStepCount='{checked(totalSubmittedRunCount * StepCount)}'");
            this.output.WriteLine(
                $"ChildRuntimeFailureInjected='{injectChildRuntimeFailure.ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"KillAfterCompletedStepCount='{(injectChildRuntimeFailure ? KillAfterCompletedStepCount : 0)}'");
            this.output.WriteLine(
                $"ChildRuntimeCrashCount='{totalChildRuntimeCrashCount}'");
            this.output.WriteLine(
                $"ParentHostFailureInjected='{injectParentHostFailure.ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"ParentHostFailureTrigger='{(injectParentHostFailure ? (waitForExternalParentHostFailure ? "external-manual" : "automatic") : "none")}'");
            this.output.WriteLine(
                $"ParentHostCrashCount='{totalParentHostCrashCount}'");
            this.output.WriteLine(
                $"RecoveredSharedRunCount='{totalRecoveredRunCount}'");
            this.output.WriteLine(
                $"RecoveryForensicsProofCount='{totalRecoveryForensicsCount}'");
            this.output.WriteLine(
                $"ReplayProofCount='{totalReplayProofCount}'");
            this.output.WriteLine(
                $"AdmissionTooManyRequestsRetryCount='{totalAdmissionTooManyRequestsRetryCount}'");
            this.output.WriteLine(
                $"ReplayAndLedgerTooManyRequestsRetryCount='{totalReplayTooManyRequestsRetryCount}'");
            this.output.WriteLine(
                $"ExecutionLedgerEntryCount='{totalExecutionLedgerEntryCount}'");
            this.output.WriteLine(
                $"ControlPlaneLedgerEntryCount='{totalControlPlaneLedgerEntryCount}'");
            this.output.WriteLine(
                $"RuntimeLifecycleLedgerEntryCount='{totalRuntimeLifecycleLedgerEntryCount}'");
            this.output.WriteLine(
                $"RawStepCompletedLedgerEntryCount='{totalRawStepCompletedLedgerEntryCount}'");
            this.output.WriteLine(
                $"DistinctLogicalStepCompletedLedgerCount='{totalDistinctLogicalStepCompletedLedgerCount}'");
            this.output.WriteLine(
                $"RecoveryCoveredDuplicateStepCompletedLedgerEntryCount='{totalRecoveryCoveredDuplicateStepCompletedLedgerEntryCount}'");
            this.output.WriteLine(
                $"FinalParentProcessCountAlive='{finalParentProcessCountAlive}'");
            this.output.WriteLine(
                $"ScenarioTotalDuration='{scenarioDuration}'");
            this.output.WriteLine(
                $"WarmPoolReusedBetweenCycles='{(executionCycleCount > 1).ToString().ToLowerInvariant()}'");
            this.output.WriteLine("IntermediateCleanupExecuted='false'");
            this.output.WriteLine("FinalCleanupPending='true'");
            this.output.WriteLine("DuplicateDispatchDetected='false'");
            this.output.WriteLine("LostRunDetected='false'");
            this.output.WriteLine("FailedRunDetected='false'");
            this.output.WriteLine("ProcessHostCapacityExceeded='false'");
            this.output.WriteLine("RuntimeCapacityExceeded='false'");
            this.output.WriteLine("ReplayValidated='true'");
            this.output.WriteLine("LedgerValidated='true'");
            this.output.WriteLine("TraceValidated='true'");
            this.output.WriteLine("RuntimeLifecycleJournalValidated='true'");
            this.output.WriteLine(
                $"RecoveryForensicsValidated='{(!recoveryInjected || totalRecoveryForensicsCount == totalRecoveredRunCount).ToString().ToLowerInvariant()}'");
        }

        private void WritePhase(
            int phaseNumber,
            int? cycleNumber,
            string title,
            string passTarget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                phaseNumber,
                1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                phaseNumber,
                6);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(passTarget);

            var cycleSegment =
                cycleNumber.HasValue
                    ? $" - CYCLE {cycleNumber.Value}"
                    : string.Empty;

            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# PHASE {phaseNumber}/6{cycleSegment} - {title}");
            this.output.WriteLine($"[PASS TARGET] {passTarget}");
        }

        private void WriteTimingSummary(
            TimeSpan setupDuration,
            IReadOnlyCollection<ProcessHostPoolProductionCycleTiming> timings,
            TimeSpan finalProofDuration,
            TimeSpan scenarioDuration,
            bool injectChildRuntimeFailure,
            bool injectParentHostFailure)
        {
            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} TIMING SUMMARY");
            this.output.WriteLine(
                $"  - Setup durable control plane, tenant MCP client, parent Process Hosts, and exact runtime topology: {setupDuration}");

            var drainTimingLabel =
                injectChildRuntimeFailure
                    ? "recover one child runtime and one distinct parent, then drain every DAG"
                    : injectParentHostFailure
                        ? "recover and drain every DAG"
                        : "drain every DAG without failure injection";

            foreach (var timing in timings.OrderBy(value => value.CycleNumber))
            {
                this.output.WriteLine(
                    $"  - Cycle {timing.CycleNumber} submit full-capacity waves: {timing.SubmissionDuration}");
                this.output.WriteLine(
                    $"  - Cycle {timing.CycleNumber} {drainTimingLabel}: {timing.DrainDuration}");
                this.output.WriteLine(
                    $"  - Cycle {timing.CycleNumber} validate bounded capacity and topology safety: {timing.SafetyDuration}");
                this.output.WriteLine(
                    $"  - Cycle {timing.CycleNumber} replay, ledger, trace, lifecycle, and forensics: {timing.ReplayLedgerTraceDuration}");
                this.output.WriteLine(
                    $"  - Cycle {timing.CycleNumber} total: {timing.TotalDuration}");
            }

            this.output.WriteLine(
                $"  - Produce final topology, run placement, performance, datastore, and safety proof: {finalProofDuration}");
            this.output.WriteLine($"  - Scenario total: {scenarioDuration}");
        }

        private static ProductionRuntimeScenarioDefinition CreateScenario(
            int totalRuntimeCount,
            int submittedRunCountPerCycle,
            bool injectChildRuntimeFailure,
            bool injectParentHostFailure,
            int executionCycleCount)
        {
            var template =
                ProductionRuntimeScenarioFactory
                    .CreateSingleTenantSharedRuntimeModeScenario();
            var templateTenant = Assert.Single(template.Tenants);
            var tenant =
                templateTenant with
                {
                    MaxRuntimeInstances = totalRuntimeCount,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    ExpectCapacityOverflow =
                        submittedRunCountPerCycle > totalRuntimeCount,
                    Run = templateTenant.Run with
                    {
                        RunCount = submittedRunCountPerCycle,
                        StepCount = StepCount,
                        DelayMs =
                            injectChildRuntimeFailure || injectParentHostFailure
                                ? 750
                                : 150,
                        FlakyStepInterval = 0,
                        EnableRetention = false
                    }
                };

            return template with
            {
                Name = string.Concat(
                    "process-host-pool-production-",
                    injectChildRuntimeFailure
                        ? "child-and-parent-failure"
                        : injectParentHostFailure
                            ? "parent-failure"
                            : "capacity",
                    "-cycles-",
                    executionCycleCount),
                ControlPlaneIdPrefix =
                    "production-process-host-pool",
                Tenants = new[] { tenant },
                SubmitMode = ProductionRuntimeSubmitMode.QueueFirst,
                AssertRetention = false,
                ScaleOutTimeout = TimeSpan.FromMinutes(5),
                DispatchTimeout = TimeSpan.FromMinutes(10),
                CompletionTimeout =
                    injectChildRuntimeFailure || injectParentHostFailure
                        ? TimeSpan.FromMinutes(60)
                        : TimeSpan.FromMinutes(45)
            };
        }

        private static TimeSpan ResolveWorkloadNoProgressTimeout(
            int submittedRunCountPerCycle)
        {
            if (submittedRunCountPerCycle >= 500)
            {
                return TimeSpan.FromMinutes(12);
            }

            if (submittedRunCountPerCycle >= 225)
            {
                return TimeSpan.FromMinutes(8);
            }

            return TimeSpan.FromMinutes(5);
        }

        private static async Task<ProcessHostPoolChildRuntimeFailureTarget>
            WaitForBusyChildRuntimeFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                ProcessHostPoolProductionCluster cluster,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastAssignedCount = 0;
            var lastRunningCount = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var currentHostIds = ReadHostIds(cluster);
                var currentMembers =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime =>
                                StringComparer.Ordinal.Equals(
                                    runtime.PoolId,
                                    cluster.PoolId) &&
                                StringComparer.Ordinal.Equals(
                                    runtime.ControlPlaneId,
                                    controlPlaneId) &&
                                !string.IsNullOrWhiteSpace(runtime.HostId) &&
                                currentHostIds.Contains(runtime.HostId!))
                        .ToArray();

                var currentMembersById =
                    currentMembers.ToDictionary(
                        runtime => runtime.RuntimeInstanceId,
                        StringComparer.Ordinal);
                var sharedRuns =
                    await ReadSubmittedSharedRunsAsync(
                            sharedRunStore,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId)
                        .ConfigureAwait(false);

                lastAssignedCount =
                    sharedRuns.Count(
                        run =>
                            !string.IsNullOrWhiteSpace(
                                run.AssignedRuntimeInstanceId) &&
                            !string.IsNullOrWhiteSpace(run.LocalRunId));
                lastRunningCount = 0;

                foreach (var sharedRun in sharedRuns
                             .Where(
                                 run =>
                                     !string.IsNullOrWhiteSpace(
                                         run.AssignedRuntimeInstanceId) &&
                                     !string.IsNullOrWhiteSpace(
                                         run.LocalRunId))
                             .OrderByDescending(run => run.UpdatedAtUtc))
                {
                    if (!currentMembersById.TryGetValue(
                            sharedRun.AssignedRuntimeInstanceId!,
                            out var runtime) ||
                        string.IsNullOrWhiteSpace(runtime.HostId) ||
                        runtime.ProcessId.GetValueOrDefault() <= 0)
                    {
                        continue;
                    }

                    var index =
                        await runExecutionIndex
                            .GetAsync(sharedRun.LocalRunId!)
                            .ConfigureAwait(false);
                    var executionId =
                        index?.ExecutionId ?? sharedRun.ExecutionId;

                    if (index is null ||
                        string.IsNullOrWhiteSpace(executionId) ||
                        !string.Equals(
                            index.Status,
                            "running",
                            StringComparison.OrdinalIgnoreCase) ||
                        !StringComparer.Ordinal.Equals(
                            index.RuntimeInstanceId,
                            runtime.RuntimeInstanceId))
                    {
                        continue;
                    }

                    lastRunningCount++;

                    var host = cluster.GetCurrentHost(runtime.HostId!);
                    var initialHostRuntimeInstanceIds =
                        host.RuntimeInstanceIds
                            .ToHashSet(StringComparer.Ordinal);
                    var siblingRuntimeInstanceIds =
                        initialHostRuntimeInstanceIds
                            .Where(
                                runtimeInstanceId =>
                                    !StringComparer.Ordinal.Equals(
                                        runtimeInstanceId,
                                        runtime.RuntimeInstanceId))
                            .ToHashSet(StringComparer.Ordinal);

                    if (siblingRuntimeInstanceIds.Count !=
                        cluster.RuntimeCountPerHost - 1)
                    {
                        continue;
                    }

                    return new ProcessHostPoolChildRuntimeFailureTarget(
                        host,
                        runtime,
                        sharedRun,
                        new ProcessHostPoolProductionActiveRun(
                            runtime.RuntimeInstanceId,
                            sharedRun.SharedRunId,
                            sharedRun.LocalRunId!,
                            executionId!,
                            index.Status),
                        siblingRuntimeInstanceIds,
                        initialHostRuntimeInstanceIds,
                        ReadHostIds(cluster),
                        ReadParentProcessIds(cluster));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"No busy child runtime exposed one durable running execution for the final ProcessHostPool failure proof. AssignedRunCount='{lastAssignedCount}', RunningRunCount='{lastRunningCount}', RuntimeCountPerHost='{cluster.RuntimeCountPerHost}'.");
        }

        private static async Task<IReadOnlyList<AiSharedRunRecord>>
            ReadSubmittedSharedRunsAsync(
                IAiSharedRunStore sharedRunStore,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId)
        {
            var recordsById =
                (await sharedRunStore
                        .ListAsync(
                            includeCancelled: true,
                            includeCompleted: true,
                            includeFailed: true)
                        .ConfigureAwait(false))
                    .Where(
                        run =>
                            submittedSharedRunIds.Contains(run.SharedRunId))
                    .ToDictionary(
                        run => run.SharedRunId,
                        StringComparer.Ordinal);

            foreach (var sharedRunId in submittedSharedRunIds)
            {
                if (recordsById.ContainsKey(sharedRunId))
                {
                    continue;
                }

                var exact =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (exact is not null)
                {
                    recordsById[sharedRunId] = exact;
                }
            }

            return recordsById.Values
                .Where(
                    run =>
                        StringComparer.Ordinal.Equals(
                            run.ControlPlaneId,
                            controlPlaneId) &&
                        StringComparer.Ordinal.Equals(
                            run.ExecutionContextSnapshot.TenantId,
                            tenantId))
                .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                .ToArray();
        }

        private static RealRuntimeCrashAssignedWorkInventoryProof
            CreateChildRuntimeFailureInventory(
                ProductionTenantScenarioDefinition tenant,
                McpTestClient mcp,
                ProcessHostPoolChildRuntimeFailureTarget target)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(target);

            return new RealRuntimeCrashAssignedWorkInventoryProof
            {
                Tenant = tenant,
                Mcp = mcp,
                RuntimeInstanceId = target.Runtime.RuntimeInstanceId,
                Works = new[]
                {
                    new RealRuntimeCrashWorkProof
                    {
                        Kind =
                            RealRuntimeCrashWorkKind.InFlightExecution,
                        SharedRun = target.SharedRun,
                        SharedRunId = target.ActiveRun.SharedRunId,
                        LocalRunId = target.ActiveRun.LocalRunId,
                        ExecutionId = target.ActiveRun.ExecutionId,
                        PipelineName =
                            target.SharedRun.PipelineKey ??
                            target.SharedRun.RunRequest.PipelineName
                    }
                }
            };
        }

        private static async Task AssertRuntimeBelongsToTenantAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            var snapshot =
                (await registry
                        .ListAsync(includeStopped: true)
                        .ConfigureAwait(false))
                    .SingleOrDefault(
                        runtime => StringComparer.Ordinal.Equals(
                            runtime.RuntimeInstanceId,
                            runtimeInstanceId));

            Assert.NotNull(snapshot);
            Assert.Equal(tenant.TenantId, snapshot!.TenantId);
        }

        private static void AssertExactChildRuntimeReplacementTopology(
            ProcessHostPoolProductionCluster cluster,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> currentTopology,
            ProcessHostPoolChildRuntimeFailureTarget target,
            RealRuntimeCrashFailedRuntimeRecoveryProof recoveryProof,
            string logPrefix,
            int cycleNumber)
        {
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(currentTopology);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(recoveryProof);

            target.Host.AssertRunning();
            Assert.True(
                target.InitialAllHostIds.SetEquals(ReadHostIds(cluster)),
                $"{logPrefix} cycle {cycleNumber} changed a parent HostId during one child runtime replacement.");
            Assert.True(
                target.InitialAllParentProcessIds.SetEquals(
                    ReadParentProcessIds(cluster)),
                $"{logPrefix} cycle {cycleNumber} changed a parent ProcessId during one child runtime replacement.");

            var currentHostRuntimeInstanceIds =
                target.Host.RuntimeInstanceIds
                    .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                cluster.RuntimeCountPerHost,
                currentHostRuntimeInstanceIds.Count);
            Assert.DoesNotContain(
                target.Runtime.RuntimeInstanceId,
                currentHostRuntimeInstanceIds);
            Assert.True(
                target.SiblingRuntimeInstanceIds.IsSubsetOf(
                    currentHostRuntimeInstanceIds),
                $"{logPrefix} cycle {cycleNumber} changed one or more sibling runtime identities during one child replacement.");

            var replacementRuntimeInstanceIds =
                currentHostRuntimeInstanceIds
                    .Except(
                        target.InitialHostRuntimeInstanceIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Single(replacementRuntimeInstanceIds);
            Assert.Single(recoveryProof.RecoveredWorks);
            Assert.Equal(
                target.Runtime.RuntimeInstanceId,
                recoveryProof.FailedInventory.RuntimeInstanceId);

            var currentTopologyRuntimeIds =
                ReadRuntimeInstanceIds(currentTopology);
            Assert.Equal(cluster.TotalRuntimeCount, currentTopologyRuntimeIds.Count);
            Assert.DoesNotContain(
                target.Runtime.RuntimeInstanceId,
                currentTopologyRuntimeIds);
            Assert.True(
                target.SiblingRuntimeInstanceIds.IsSubsetOf(
                    currentTopologyRuntimeIds),
                $"{logPrefix} cycle {cycleNumber} lost a sibling runtime after one exact child failure.");

            var targetHostMembers =
                currentTopology
                    .Where(
                        runtime => StringComparer.Ordinal.Equals(
                            runtime.HostId,
                            target.Host.HostId))
                    .Select(runtime => runtime.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                currentHostRuntimeInstanceIds.SetEquals(targetHostMembers),
                $"{logPrefix} cycle {cycleNumber} readiness and registry disagree after child runtime replacement.");
        }

        private sealed record ProcessHostPoolChildRuntimeFailureTarget(
            ProcessHostPoolProductionHostProcess Host,
            AiRuntimeInstanceSnapshot Runtime,
            AiSharedRunRecord SharedRun,
            ProcessHostPoolProductionActiveRun ActiveRun,
            IReadOnlySet<string> SiblingRuntimeInstanceIds,
            IReadOnlySet<string> InitialHostRuntimeInstanceIds,
            IReadOnlySet<string> InitialAllHostIds,
            IReadOnlySet<int> InitialAllParentProcessIds);

        private sealed class ProcessHostPoolChildRuntimeProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly string poolId;
            private readonly string hostId;
            private readonly int parentProcessId;
            private readonly ITestOutputHelper output;
            private readonly string logPrefix;

            public ProcessHostPoolChildRuntimeProcessControl(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                string hostId,
                int parentProcessId,
                ITestOutputHelper output,
                string logPrefix)
            {
                this.registry = registry;
                this.poolId = poolId;
                this.hostId = hostId;
                this.parentProcessId = parentProcessId;
                this.output = output;
                this.logPrefix = logPrefix;
            }

            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    (await this.registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .SingleOrDefault(
                            runtime => StringComparer.Ordinal.Equals(
                                runtime.RuntimeInstanceId,
                                runtimeInstanceId));

                if (snapshot is null ||
                    !StringComparer.Ordinal.Equals(
                        snapshot.PoolId,
                        this.poolId) ||
                    !StringComparer.Ordinal.Equals(
                        snapshot.HostId,
                        this.hostId) ||
                    snapshot.ProcessId.GetValueOrDefault() <= 0)
                {
                    throw new InvalidOperationException(
                        $"The ProcessHostPool child runtime kill target is no longer an active member of the expected parent. RuntimeInstanceId='{runtimeInstanceId}', PoolId='{snapshot?.PoolId}', HostId='{snapshot?.HostId}', ProcessId='{snapshot?.ProcessId}'.");
                }

                var childProcessId = snapshot.ProcessId!.Value;

                if (childProcessId == this.parentProcessId)
                {
                    throw new InvalidOperationException(
                        $"The child runtime kill target resolved to the parent ProcessHost PID. RuntimeInstanceId='{runtimeInstanceId}', ProcessId='{childProcessId}'.");
                }

                using var process = Process.GetProcessById(childProcessId);

                this.output.WriteLine(
                    $"[{this.logPrefix} CHILD RUNTIME PROCESS KILL] RuntimeInstanceId='{runtimeInstanceId}', HostId='{this.hostId}', ParentProcessId='{this.parentProcessId}', ChildProcessId='{childProcessId}'.");

                process.Kill(entireProcessTree: true);

                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));

                await process
                    .WaitForExitAsync(timeout.Token)
                    .ConfigureAwait(false);

                return true;
            }
        }

        private static async Task<ProcessHostPoolProductionFailureTarget>
            WaitForBusyParentHostFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                ProcessHostPoolProductionCluster cluster,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                TimeSpan timeout,
                IReadOnlySet<string>? excludedHostIds = null)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            var lastAssignedCount = 0;
            var lastActiveCount = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var currentHostIds = ReadHostIds(cluster);
                var currentMembers =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime =>
                                StringComparer.Ordinal.Equals(
                                    runtime.PoolId,
                                    cluster.PoolId) &&
                                StringComparer.Ordinal.Equals(
                                    runtime.ControlPlaneId,
                                    controlPlaneId) &&
                                !string.IsNullOrWhiteSpace(runtime.HostId) &&
                                currentHostIds.Contains(runtime.HostId!))
                        .ToArray();

                var sharedRuns =
                    (await ReadSubmittedSharedRunsAsync(
                            sharedRunStore,
                            submittedSharedRunIds,
                            controlPlaneId,
                            tenantId)
                        .ConfigureAwait(false))
                        .Where(
                            run =>
                                !string.IsNullOrWhiteSpace(
                                    run.AssignedRuntimeInstanceId) &&
                                !string.IsNullOrWhiteSpace(run.LocalRunId))
                        .ToArray();

                lastAssignedCount = sharedRuns.Length;
                lastActiveCount = 0;

                foreach (var host in cluster.Hosts.OrderBy(value => value.Ordinal))
                {
                    if (excludedHostIds?.Contains(host.HostId) == true)
                    {
                        continue;
                    }

                    var members =
                        currentMembers
                            .Where(
                                runtime => StringComparer.Ordinal.Equals(
                                    runtime.HostId,
                                    host.HostId))
                            .OrderBy(
                                runtime => runtime.RuntimeInstanceId,
                                StringComparer.Ordinal)
                            .ToArray();

                    if (members.Length != cluster.RuntimeCountPerHost)
                    {
                        continue;
                    }

                    var memberIds =
                        members
                            .Select(member => member.RuntimeInstanceId)
                            .ToHashSet(StringComparer.Ordinal);
                    var activeRuns =
                        new List<ProcessHostPoolProductionActiveRun>(
                            cluster.RuntimeCountPerHost);

                    foreach (var sharedRun in sharedRuns
                                 .Where(
                                     run => memberIds.Contains(
                                         run.AssignedRuntimeInstanceId!))
                                 .OrderByDescending(run => run.UpdatedAtUtc))
                    {
                        var index =
                            await runExecutionIndex
                                .GetAsync(sharedRun.LocalRunId!)
                                .ConfigureAwait(false);
                        var executionId =
                            index?.ExecutionId ?? sharedRun.ExecutionId;

                        if (index is null ||
                            string.IsNullOrWhiteSpace(executionId) ||
                            !string.Equals(
                                index.Status,
                                "running",
                                StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(index.RuntimeInstanceId) ||
                            !memberIds.Contains(index.RuntimeInstanceId))
                        {
                            continue;
                        }

                        if (activeRuns.Any(
                                run => StringComparer.Ordinal.Equals(
                                    run.RuntimeInstanceId,
                                    index.RuntimeInstanceId)))
                        {
                            continue;
                        }

                        activeRuns.Add(
                            new ProcessHostPoolProductionActiveRun(
                                index.RuntimeInstanceId,
                                sharedRun.SharedRunId,
                                sharedRun.LocalRunId!,
                                executionId!,
                                index.Status));
                    }

                    lastActiveCount = Math.Max(
                        lastActiveCount,
                        activeRuns.Count);

                    if (activeRuns.Count != cluster.RuntimeCountPerHost ||
                        !memberIds.SetEquals(
                            activeRuns.Select(run => run.RuntimeInstanceId)))
                    {
                        continue;
                    }

                    var survivingHosts =
                        cluster.Hosts
                            .Where(
                                candidate =>
                                    !StringComparer.Ordinal.Equals(
                                        candidate.HostId,
                                        host.HostId))
                            .ToArray();

                    return new ProcessHostPoolProductionFailureTarget(
                        host,
                        members,
                        activeRuns,
                        survivingHosts
                            .Select(candidate => candidate.HostId)
                            .ToHashSet(StringComparer.Ordinal),
                        survivingHosts
                            .Select(candidate => candidate.ProcessId)
                            .ToHashSet(),
                        survivingHosts
                            .SelectMany(
                                candidate => candidate.RuntimeInstanceIds)
                            .ToHashSet(StringComparer.Ordinal));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"No busy parent Process Host exposed one running execution per exact runtime member. AssignedRunCount='{lastAssignedCount}', MaximumActiveRuntimeCountOnOneHost='{lastActiveCount}', RuntimeCountPerHost='{cluster.RuntimeCountPerHost}'.");
        }

        private static async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>>
            WaitForExactTopologyAsync(
                IAiRuntimeInstanceRegistry registry,
                ProcessHostPoolProductionCluster cluster,
                string controlPlaneId,
                string providerName,
                bool requireAvailableCapacity,
                TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            IReadOnlyList<AiRuntimeInstanceSnapshot> last =
                Array.Empty<AiRuntimeInstanceSnapshot>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var currentHostIds = ReadHostIds(cluster);

                last =
                    (await registry
                            .ListAsync(includeStopped: false)
                            .ConfigureAwait(false))
                        .Where(
                            runtime =>
                                StringComparer.Ordinal.Equals(
                                    runtime.PoolId,
                                    cluster.PoolId) &&
                                StringComparer.Ordinal.Equals(
                                    runtime.ControlPlaneId,
                                    controlPlaneId) &&
                                !string.IsNullOrWhiteSpace(runtime.HostId) &&
                                currentHostIds.Contains(runtime.HostId!))
                        .ToArray();

                if (TryValidateTopology(
                        last,
                        cluster,
                        providerName,
                        requireAvailableCapacity,
                        out _))
                {
                    return last;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            _ = TryValidateTopology(
                last,
                cluster,
                providerName,
                requireAvailableCapacity,
                out var diagnostics);

            throw new TimeoutException(
                $"The MCP control plane did not observe the exact current multi-host ProcessPool topology. {diagnostics}");
        }

        private static bool TryValidateTopology(
            IReadOnlyList<AiRuntimeInstanceSnapshot> runtimes,
            ProcessHostPoolProductionCluster cluster,
            string providerName,
            bool requireAvailableCapacity,
            out string diagnostics)
        {
            var expectedRuntimeCount = cluster.TotalRuntimeCount;
            var groups =
                runtimes
                    .Where(runtime => !string.IsNullOrWhiteSpace(runtime.HostId))
                    .GroupBy(runtime => runtime.HostId!, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToArray(),
                        StringComparer.Ordinal);
            var failures = new List<string>();

            if (runtimes.Count != expectedRuntimeCount)
            {
                failures.Add(
                    $"RuntimeCount expected='{expectedRuntimeCount}' actual='{runtimes.Count}'");
            }

            if (groups.Count != cluster.Hosts.Count)
            {
                failures.Add(
                    $"HostCount expected='{cluster.Hosts.Count}' actual='{groups.Count}'");
            }

            foreach (var host in cluster.Hosts)
            {
                if (!groups.TryGetValue(host.HostId, out var hostRuntimes))
                {
                    failures.Add($"HostId missing='{host.HostId}'");
                    continue;
                }

                if (hostRuntimes.Length != cluster.RuntimeCountPerHost)
                {
                    failures.Add(
                        $"HostId='{host.HostId}' runtime count expected='{cluster.RuntimeCountPerHost}' actual='{hostRuntimes.Length}'");
                }

                var observedRuntimeIds =
                    hostRuntimes
                        .Select(runtime => runtime.RuntimeInstanceId)
                        .ToHashSet(StringComparer.Ordinal);

                if (!host.RuntimeInstanceIds.SetEquals(observedRuntimeIds))
                {
                    failures.Add(
                        $"HostId='{host.HostId}' readiness/registry runtime identity mismatch");
                }

                foreach (var runtime in hostRuntimes)
                {
                    runtime.Metadata.TryGetValue(
                        AiRuntimeInstanceProviderMetadataKeys.ProviderName,
                        out var observedProviderName);
                    runtime.Metadata.TryGetValue(
                        AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint,
                        out var observedEndpoint);

                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                            observedProviderName,
                            providerName))
                    {
                        failures.Add(
                            $"RuntimeInstanceId='{runtime.RuntimeInstanceId}' provider expected='{providerName}' actual='{observedProviderName}'");
                    }

                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                            observedEndpoint?.TrimEnd('/'),
                            host.StableTransportEndpoint.TrimEnd('/')))
                    {
                        failures.Add(
                            $"RuntimeInstanceId='{runtime.RuntimeInstanceId}' endpoint expected='{host.StableTransportEndpoint}' actual='{observedEndpoint}'");
                    }

                    if (runtime.ProcessId.GetValueOrDefault() <= 0)
                    {
                        failures.Add(
                            $"RuntimeInstanceId='{runtime.RuntimeInstanceId}' does not expose a child ProcessId.");
                    }

                    if (requireAvailableCapacity &&
                        (!runtime.CanAcceptRun ||
                         runtime.IsQueuePaused ||
                         runtime.AvailableRunSlots.GetValueOrDefault() <= 0))
                    {
                        failures.Add(
                            $"RuntimeInstanceId='{runtime.RuntimeInstanceId}' is not admission-ready. Status='{runtime.Status}', CanAcceptRun='{runtime.CanAcceptRun}', IsQueuePaused='{runtime.IsQueuePaused}', AvailableRunSlots='{runtime.AvailableRunSlots}', ProcessId='{runtime.ProcessId}'");
                    }
                }
            }

            diagnostics =
                failures.Count == 0
                    ? "Topology is exact."
                    : string.Join(" | ", failures.Take(40));

            return failures.Count == 0;
        }

        private static void AssertExactReplacementTopology(
            ProcessHostPoolProductionCluster cluster,
            IReadOnlyList<AiRuntimeInstanceSnapshot> currentTopology,
            ProcessHostPoolProductionRecoveryProof proof,
            string logPrefix,
            int cycleNumber)
        {
            Assert.False(proof.FailedHost.IsRunning);
            Assert.True(proof.ReplacementHost.IsRunning);
            Assert.NotEqual(
                proof.FailedHost.HostId,
                proof.ReplacementHost.HostId);
            Assert.NotEqual(
                proof.FailedHost.ProcessId,
                proof.ReplacementHost.ProcessId);

            var currentHostIds = ReadHostIds(cluster);
            var expectedHostIds =
                proof.SurvivingHostIds
                    .Append(proof.ReplacementHost.HostId)
                    .ToHashSet(StringComparer.Ordinal);

            RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                expectedHostIds,
                currentHostIds,
                $"{logPrefix} cycle {cycleNumber} exact parent Host replacement proof");

            var currentParentProcessIds = ReadParentProcessIds(cluster);
            var expectedParentProcessIds =
                proof.SurvivingParentProcessIds
                    .Append(proof.ReplacementHost.ProcessId)
                    .ToHashSet();

            Assert.True(
                expectedParentProcessIds.SetEquals(
                    currentParentProcessIds),
                $"{logPrefix} cycle {cycleNumber} changed one or more surviving parent ProcessIds.");

            var currentRuntimeInstanceIds =
                ReadRuntimeInstanceIds(currentTopology);
            var expectedRuntimeInstanceIds =
                proof.SurvivingRuntimeInstanceIds
                    .Concat(proof.ReplacementRuntimeInstanceIds)
                    .ToHashSet(StringComparer.Ordinal);

            RuntimePoolProductionCycleExecutor.AssertSameIdentitySet(
                expectedRuntimeInstanceIds,
                currentRuntimeInstanceIds,
                $"{logPrefix} cycle {cycleNumber} exact runtime membership replacement proof");

            Assert.Empty(
                proof.FailedRuntimeInstanceIds.Intersect(
                    currentRuntimeInstanceIds,
                    StringComparer.Ordinal));
            Assert.Equal(
                cluster.RuntimeCountPerHost,
                proof.ReplacementRuntimeInstanceIds.Count);
            Assert.Equal(cluster.TotalRuntimeCount, currentRuntimeInstanceIds.Count);
        }

        private static async Task AssertRecoveryForensicsAsync(
            IAiRuntimeRecoveryForensicsQueryService queryService,
            ProcessHostPoolProductionRecoveryProof recoveryProof,
            TimeSpan timeout)
        {
            Assert.Equal(
                recoveryProof.RecoveredExecutionIds.Count,
                recoveryProof.RecoveryForensicsIds.Count);

            foreach (var forensicsId in recoveryProof.RecoveryForensicsIds
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                var record =
                    await WaitForRecoveryForensicsAsync(
                            queryService,
                            forensicsId,
                            timeout)
                        .ConfigureAwait(false);

                Assert.Equal(
                    recoveryProof.FailureId,
                    record.RuntimeFailureIncidentId);
                Assert.Contains(
                    record.ExecutionId,
                    recoveryProof.RecoveredExecutionIds);
                Assert.NotNull(record.SharedRunId);
                Assert.Contains(
                    record.SharedRunId!,
                    recoveryProof.RecoveredSharedRunIds);
            }
        }

        private static async Task<AiRuntimeRecoveryForensicsReadModel>
            WaitForRecoveryForensicsAsync(
                IAiRuntimeRecoveryForensicsQueryService queryService,
                string forensicsId,
                TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var record =
                    await queryService
                        .GetByForensicsIdAsync(forensicsId)
                        .ConfigureAwait(false);

                if (record is not null)
                {
                    return record;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Recovery forensics '{forensicsId}' did not become visible.");
        }

        private static IReadOnlySet<string> ReadHostIds(
            ProcessHostPoolProductionCluster cluster)
        {
            return cluster.Hosts
                .Select(host => host.HostId)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static IReadOnlySet<int> ReadParentProcessIds(
            ProcessHostPoolProductionCluster cluster)
        {
            return cluster.Hosts
                .Select(host => host.ProcessId)
                .ToHashSet();
        }

        private static IReadOnlySet<string> ReadRuntimeInstanceIds(
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> topology)
        {
            return topology
                .Select(runtime => runtime.RuntimeInstanceId)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static async Task<IReadOnlyList<AiDecisionLedgerEntry>>
            QueryExecutionLedgerAsync(
                McpTestClient mcp,
                IReadOnlySet<string> executionIds,
                DateTimeOffset fromUtc,
                DateTimeOffset toUtc,
                string tenantId,
                Action<string, int, TimeSpan>? onBackpressureRetry = null)
        {
            var entries = new List<AiDecisionLedgerEntry>();

            foreach (var batch in executionIds
                         .OrderBy(value => value, StringComparer.Ordinal)
                         .Chunk(8))
            {
                var results =
                    await Task.WhenAll(
                            batch.Select(
                                executionId =>
                                    McpBackpressureRetryHelper.ExecuteAsync(
                                        () => mcp.QueryLedgerAsync(
                                            new AiDecisionLedgerQuery
                                            {
                                                ExecutionId = executionId,
                                                TimestampFromUtc = fromUtc,
                                                TimestampToUtc = toUtc
                                            }),
                                        $"process-host-pool-ledger:{tenantId}:{executionId}",
                                        onRetry: onBackpressureRetry)))
                        .ConfigureAwait(false);

                Assert.All(
                    results,
                    batchEntries => Assert.NotEmpty(batchEntries));
                entries.AddRange(results.SelectMany(value => value));
            }

            return entries;
        }

        private static async Task<IReadOnlyList<AiDecisionLedgerEntry>>
            QueryControlPlaneRunLedgerAsync(
                McpTestClient mcp,
                IReadOnlySet<string> sharedRunIds,
                DateTimeOffset fromUtc,
                DateTimeOffset toUtc,
                string tenantId,
                Action<string, int, TimeSpan>? onBackpressureRetry = null)
        {
            var entries = new List<AiDecisionLedgerEntry>();

            foreach (var batch in sharedRunIds
                         .OrderBy(value => value, StringComparer.Ordinal)
                         .Chunk(8))
            {
                var results =
                    await Task.WhenAll(
                            batch.Select(
                                sharedRunId =>
                                    McpBackpressureRetryHelper.ExecuteAsync(
                                        () => mcp.QueryLedgerAsync(
                                            new AiDecisionLedgerQuery
                                            {
                                                ExecutionId =
                                                    $"control-plane-run:{sharedRunId}",
                                                TimestampFromUtc = fromUtc,
                                                TimestampToUtc = toUtc
                                            }),
                                        $"process-host-pool-control-plane-ledger:{tenantId}:{sharedRunId}",
                                        onRetry: onBackpressureRetry)))
                        .ConfigureAwait(false);

                Assert.All(
                    results,
                    batchEntries => Assert.NotEmpty(batchEntries));
                entries.AddRange(results.SelectMany(value => value));
            }

            return entries;
        }

        private static async Task<IReadOnlyList<AiDecisionLedgerEntry>>
            QueryRuntimeLifecycleLedgerAsync(
                McpTestClient mcp,
                IReadOnlySet<string> runtimeInstanceIds,
                DateTimeOffset fromUtc,
                DateTimeOffset toUtc,
                string tenantId,
                Action<string, int, TimeSpan>? onBackpressureRetry = null)
        {
            var entries = new List<AiDecisionLedgerEntry>();

            foreach (var batch in runtimeInstanceIds
                         .OrderBy(value => value, StringComparer.Ordinal)
                         .Chunk(8))
            {
                var results =
                    await Task.WhenAll(
                            batch.Select(
                                runtimeInstanceId =>
                                    McpBackpressureRetryHelper.ExecuteAsync(
                                        () => mcp.QueryLedgerAsync(
                                            new AiDecisionLedgerQuery
                                            {
                                                ExecutionId =
                                                    $"control-plane-runtime-instance:{runtimeInstanceId}",
                                                TimestampFromUtc = fromUtc,
                                                TimestampToUtc = toUtc
                                            }),
                                        $"process-host-pool-runtime-lifecycle-ledger:{tenantId}:{runtimeInstanceId}",
                                        onRetry: onBackpressureRetry)))
                        .ConfigureAwait(false);

                entries.AddRange(results.SelectMany(value => value));
            }

            return entries;
        }

        private static void WriteTopology(
            string phase,
            ProcessHostPoolProductionCluster cluster,
            IReadOnlyList<AiRuntimeInstanceSnapshot> runtimes,
            ITestOutputHelper output,
            string logPrefix)
        {
            output.WriteLine($"# {logPrefix} {phase} TOPOLOGY");

            foreach (var host in cluster.Hosts.OrderBy(value => value.Ordinal))
            {
                var hostRuntimes =
                    runtimes
                        .Where(
                            runtime =>
                                StringComparer.Ordinal.Equals(
                                    runtime.HostId,
                                    host.HostId))
                        .OrderBy(
                            runtime => runtime.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                output.WriteLine(
                    $"HostOrdinal='{host.Ordinal}', ParentProcessId='{host.ProcessId}', HostId='{host.HostId}', StableTransportEndpoint='{host.StableTransportEndpoint}', RuntimeCount='{hostRuntimes.Length}'.");

                foreach (var runtime in hostRuntimes)
                {
                    output.WriteLine(
                        $"  RuntimeInstanceId='{runtime.RuntimeInstanceId}', ChildProcessId='{runtime.ProcessId}', Status='{runtime.Status}', CanAcceptRun='{runtime.CanAcceptRun}', AvailableRunSlots='{runtime.AvailableRunSlots}'.");
                }
            }
        }

        private void WriteScenarioHeader(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount,
            int totalRuntimeCount,
            int submittedRunCountPerCycle,
            int logicalStepCountPerCycle,
            string controlPlaneId,
            string poolId,
            bool injectChildRuntimeFailure,
            bool injectParentHostFailure,
            bool waitForExternalParentHostFailure)
        {
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} PRODUCTION PROOF");
            this.output.WriteLine(
                injectChildRuntimeFailure
                    ? "Executive proof: several independent external parent Process Hosts share one logical ProcessPool; one exact child runtime is killed after durable progress while its parent and siblings survive, then one distinct fully busy parent and its runtime tree are killed, replaced, and recovered without lost work or cross-host contamination."
                    : injectParentHostFailure
                        ? "Executive proof: several independent external parent Process Hosts share one logical ProcessPool; one fully busy parent and its runtime-process tree are force-killed, exact membership is replaced, impacted work is recovered once, and the pool drains without lost work or cross-host contamination."
                        : "Executive proof: several independent external parent Process Hosts share one logical ProcessPool; each host owns several independently identifiable runtime child processes, exactly matching the Pod × runtime hierarchy without Kubernetes.");
            this.output.WriteLine(string.Empty);
            this.output.WriteLine("Scenario contract:");
            this.output.WriteLine(
                "  - [ON] Every submission is persisted through QueueFirst shared-queue admission.");
            this.output.WriteLine(
                "  - [ON] Existing ProcessHostPool capacity is reused before any replacement capacity is introduced.");
            this.output.WriteLine(
                "  - [ON] Parent Process Hosts and child runtime membership never exceed their configured bounds.");
            this.output.WriteLine(
                "  - [ON] Every shared run resolves to exactly one local run and one durable DAG execution.");
            this.output.WriteLine(
                $"  - [ON] Every DAG completes exactly {StepCount} logical steps.");
            this.output.WriteLine(
                injectChildRuntimeFailure
                    ? $"  - [ON] One child runtime is killed after at least {KillAfterCompletedStepCount} completed steps while its parent and siblings survive; one distinct fully busy parent and its child tree are then force-killed; both recoveries are validated."
                    : injectParentHostFailure
                        ? "  - [ON] One fully busy parent Process Host and its child-process tree are force-killed; exact membership suppression, replacement, recovery, replay, ledger, trace, forensics, and topology are validated."
                        : "  - [ON] Replay, ledger, trace, durable lifecycle topology, datastore traffic, and no-recovery contamination are validated.");
            this.output.WriteLine(string.Empty);
            this.output.WriteLine("Workload summary:");
            this.output.WriteLine(
                $"  MaximumConfiguredProcessHostCount='{maximumProcessHostCount}'");
            this.output.WriteLine(
                $"  RuntimeCountPerHost='{runtimeCountPerHost}'");
            this.output.WriteLine(
                $"  MaximumRuntimeCapacity='{totalRuntimeCount}'");
            this.output.WriteLine(
                $"  SubmissionIterationCountPerCycle='{submissionIterationCount}'");
            this.output.WriteLine(
                $"  RunsPerIteration='{totalRuntimeCount}'");
            this.output.WriteLine(
                $"  SubmittedRunCountPerCycle='{submittedRunCountPerCycle}'");
            this.output.WriteLine(
                $"  LogicalStepCountPerCycle='{logicalStepCountPerCycle}'");
            this.output.WriteLine(
                $"  ExecutionCycleCount='{executionCycleCount}'");
            this.output.WriteLine(
                $"  MaximumConcurrentMcpSubmissions='{Math.Clamp(totalRuntimeCount, 4, 16)}'");
            this.output.WriteLine(
                $"  MaximumAdmissionAttemptCount='{MaximumAdmissionAttemptCount}'");
            this.output.WriteLine(string.Empty);
            this.output.WriteLine("Runtime profile:");
            this.output.WriteLine(
                $"  Provider='{this.profile.ProviderName}'");
            this.output.WriteLine(
                $"  ControlPlaneId='{controlPlaneId}'");
            this.output.WriteLine($"  PoolId='{poolId}'");
            this.output.WriteLine("  HostCreationMode='Process'");
            this.output.WriteLine("  PersistenceProfile='MongoRedis'");
            this.output.WriteLine("  ObservabilityProfile='DurableMongo'");
            this.output.WriteLine("  SubmitMode='QueueFirst'");
            this.output.WriteLine(
                $"  InjectChildRuntimeFailure='{injectChildRuntimeFailure}'");
            this.output.WriteLine(
                $"  KillAfterCompletedStepCount='{(injectChildRuntimeFailure ? KillAfterCompletedStepCount : 0)}'");
            this.output.WriteLine(
                $"  InjectParentHostFailure='{injectParentHostFailure}'");
            this.output.WriteLine(
                $"  ParentHostFailureTrigger='{(injectParentHostFailure ? (waitForExternalParentHostFailure ? "external-manual" : "automatic") : "none")}'");
            this.output.WriteLine(
                "  TopologyContract='ProcessHostCount × RuntimeCountPerHost'");
            this.output.WriteLine(
                "  CleanupPolicy='after-final-cycle-only'");
        }

        private sealed record ProcessHostPoolProductionCycleTiming(
            int CycleNumber,
            TimeSpan SubmissionDuration,
            TimeSpan DrainDuration,
            TimeSpan SafetyDuration,
            TimeSpan ReplayLedgerTraceDuration,
            TimeSpan TotalDuration);
    }

    /// <summary>
    /// Serializes multi-host ProcessPool proofs because they own real loopback ports and process
    /// trees.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ProcessHostPoolProductionCollection
    {
        /// <summary>
        /// Gets the shared collection name.
        /// </summary>
        public const string Name =
            "Process Host Pool production proof collection";
    }
}
