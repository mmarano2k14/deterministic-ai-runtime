using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners;
using Multiplexed.AI.Stores;
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
        private const int MaximumAdmissionAttemptCount = 8;
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
                injectParentHostFailure: true);
        }

        private async Task ExecuteScenarioAsync(
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            int submissionIterationCount,
            int executionCycleCount,
            bool injectParentHostFailure)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                maximumProcessHostCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                runtimeCountPerHost);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                submissionIterationCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                executionCycleCount);

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
                injectParentHostFailure);

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
                                submissionIterationCount,
                            maximumConcurrentSubmissions:
                                Math.Clamp(totalRuntimeCount, 4, 16),
                            maximumAdmissionAttemptCount:
                                MaximumAdmissionAttemptCount,
                            cycleNumber:
                                executionCycleCount > 1
                                    ? cycleNumber
                                    : null)
                        .ConfigureAwait(false);
                submissionStopwatch.Stop();

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
                    $"[{this.profile.LogPrefix} MCP ADMISSION] Cycle='{cycleNumber}', SubmittedRunCount='{admission.SharedRunIds.Count}', FullCapacityWaveCount='{submissionIterationCount}', RunsPerWave='{totalRuntimeCount}', TooManyRequestsRetryCount='{admission.TooManyRequestsRetryCount}'.");
                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='Submit full-capacity waves', Duration='{submissionStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                this.WritePhase(
                    phaseNumber: 3,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title:
                        injectParentHostFailure
                            ? "FORCE-KILL ONE BUSY PARENT HOST, RECOVER, AND DRAIN EVERY DAG"
                            : "DRAIN EVERY DAG WITHOUT FAILURE INJECTION",
                    passTarget:
                        injectParentHostFailure
                            ? "Kill one parent Process Host only after its runtimes own active work, suppress that exact membership, start one fresh replacement parent, recover only impacted runs once, and complete all 50 DAG steps."
                            : "Use the full bounded ProcessHostPool capacity, preserve every parent and runtime identity, and complete every submitted DAG with exactly 50 logical steps.");

                var drainStopwatch = Stopwatch.StartNew();
                ProcessHostPoolProductionFailureTarget? failureTarget = null;
                ProcessHostPoolProductionRecoveryProof? recoveryProof = null;

                if (injectParentHostFailure)
                {
                    failureTarget =
                        await WaitForBusyParentHostFailureTargetAsync(
                                registry,
                                sharedRunStore,
                                runExecutionIndex,
                                cluster,
                                admission.SharedRunIds,
                                controlPlaneId,
                                tenant.TenantId,
                                TimeSpan.FromMinutes(5))
                            .ConfigureAwait(false);

                    recoveryProof =
                        await recoveryCoordinator
                            .RecoverAsync(
                                cluster,
                                failureTarget,
                                cycleNumber,
                                $"mcp-{this.profile.ProviderName}-process-host-pool-cycle-{cycleNumber}",
                                TimeSpan.FromMinutes(5))
                            .ConfigureAwait(false);

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
                        $"CYCLE {cycleNumber} REPLACEMENT",
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
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='{(injectParentHostFailure ? "Force-kill one busy parent, recover, and drain every DAG" : "Drain every DAG without failure injection")}', Duration='{drainStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

                this.WritePhase(
                    phaseNumber: 4,
                    cycleNumber:
                        executionCycleCount > 1
                            ? cycleNumber
                            : null,
                    title:
                        injectParentHostFailure
                            ? "BOUNDED CAPACITY AND EXACT PARENT-HOST RECOVERY SAFETY PROOF"
                            : "BOUNDED CAPACITY AND STABLE TOPOLOGY SAFETY PROOF",
                    passTarget:
                        injectParentHostFailure
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
                    $"[{this.profile.LogPrefix} TIMING] Cycle='{cycleNumber}', Phase='{(injectParentHostFailure ? "Validate bounded capacity, exact parent-host recovery, workload drain, and membership convergence" : "Validate bounded capacity, stable parent/runtime identities, and workload drain")}', Duration='{safetyStopwatch.Elapsed}', TotalElapsed='{cycleStopwatch.Elapsed}'.");

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

                var recoveryRuntimeInstanceIds =
                    recoveryProof is null
                        ? Array.Empty<string>()
                        : recoveryProof.FailedRuntimeInstanceIds
                            .Concat(
                                recoveryProof.ReplacementRuntimeInstanceIds)
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

                if (recoveryProof is null)
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

                var recoveredExecutionIds =
                    recoveryProof?.RecoveredExecutionIds ??
                    new HashSet<string>(StringComparer.Ordinal);
                var recoveredSharedRunIds =
                    recoveryProof?.RecoveredSharedRunIds ??
                    new HashSet<string>(StringComparer.Ordinal);

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

                if (recoveryProof is null)
                {
                    Assert.Equal(
                        logicalStepCountPerCycle,
                        stepLedgerProof.RawStepCompletedEntryCount);
                    Assert.Equal(0, stepLedgerProof.DuplicateStepCompletedEntryCount);
                }
                else
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

                var recoveryForensicsIds =
                    recoveryProof?.RecoveryForensicsIds
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray() ??
                    Array.Empty<string>();

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} RECOVERY FORENSICS PROOF] Cycle='{cycleNumber}', FailureId='{recoveryProof?.FailureId ?? string.Empty}', RecoveryForensicsCount='{recoveryForensicsIds.Length}', RecoveryForensicsIds='{string.Join(",", recoveryForensicsIds)}'.");
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
                        failureTarget,
                        recoveryProof,
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
                    recoveryProof,
                    cycleFinalRuntimeInstanceIds.Count,
                    replayTooManyRequestsRetryCount,
                    cycleTiming);

                this.output.WriteLine(
                    $"[{this.profile.LogPrefix} CYCLE COMPLETE] Cycle='{cycleNumber}', SubmittedRunCount='{admission.SharedRunIds.Count}', CompletedRunCount='{finalStatuses.Count}', ReplayProofCount='{replayProofs.Count}', ProcessHostCount='{cluster.Hosts.Count}', RuntimeCount='{cycleFinalRuntimeInstanceIds.Count}', ParentHostCrashCount='{(recoveryProof is null ? 0 : 1)}', RecoveredRunCount='{recoveredSharedRunIds.Count}', Duration='{cycleStopwatch.Elapsed}'.");
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
                                [tenant.TenantId] = injectParentHostFailure
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
            Assert.Equal(
                injectParentHostFailure
                    ? checked(runtimeCountPerHost * executionCycleCount)
                    : 0,
                totalRecoveredRunCount);
            Assert.Equal(
                injectParentHostFailure
                    ? executionCycleCount
                    : 0,
                totalParentHostCrashCount);

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
                injectParentHostFailure);

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
                injectParentHostFailure);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} FINAL PRODUCTION RESULT] ExecutionCycleCount='{executionCycleCount}', ProcessHostCount='{maximumProcessHostCount}', RuntimeCountPerHost='{runtimeCountPerHost}', TotalRuntimeCount='{totalRuntimeCount}', SubmissionIterationCountPerCycle='{submissionIterationCount}', TotalSubmittedRunCount='{totalSubmittedRunCount}', TotalCompletedRunCount='{totalCompletedRunCount}', TotalLogicalStepCount='{checked(totalSubmittedRunCount * StepCount)}', TotalReplayProofCount='{totalReplayProofCount}', ParentHostCrashCount='{totalParentHostCrashCount}', RecoveredRunCount='{totalRecoveredRunCount}', FinalParentProcessCountAlive='{cluster.Hosts.Count(host => host.IsRunning)}', DurationBeforeCleanup='{scenarioStopwatch.Elapsed}', CleanupPolicy='after-final-cycle-only'.");
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
                ProcessHostPoolProductionFailureTarget? failureTarget,
                ProcessHostPoolProductionRecoveryProof? recoveryProof,
                bool injectParentHostFailure)
        {
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(completedRuns);

            var initialActiveRuns =
                failureTarget?.ActiveRuns.ToDictionary(
                    run => run.SharedRunId,
                    StringComparer.Ordinal) ??
                new Dictionary<string, ProcessHostPoolProductionActiveRun>(
                    StringComparer.Ordinal);

            return completedRuns
                .OrderBy(run => run.SharedRunId, StringComparer.Ordinal)
                .Select(
                    run =>
                    {
                        initialActiveRuns.TryGetValue(
                            run.SharedRunId,
                            out var initialActiveRun);

                        string? forensicsId = null;

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
                                forensicsId = expectedForensicsId;
                            }
                        }

                        return new ProductionRuntimeRunPlacement
                        {
                            TenantId = tenant.TenantId,
                            TenantRole = injectParentHostFailure
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
                                initialActiveRun is null
                                    ? null
                                    : recoveryProof?.FailureId,
                            ForensicsId = forensicsId
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
                $"RecoveredSharedRunCount='{recoveryProof?.RecoveredSharedRunIds.Count ?? 0}'");
            this.output.WriteLine(
                $"RecoveryForensicsCount='{recoveryProof?.RecoveryForensicsIds.Count ?? 0}'");
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
                $"  DuplicateStepLedgerEvidenceOutsideRecoveryDetected='{(stepLedgerProof.DuplicateStepCompletedEntryCount > 0 && recoveryProof is null).ToString().ToLowerInvariant()}'");
            this.output.WriteLine("  TraceValidated='true'");
            this.output.WriteLine(
                $"  ExactParentHostRecoveryValidated='{(recoveryProof is null || recoveryProof.RecoveredSharedRunIds.Count == runtimeCountPerHost).ToString().ToLowerInvariant()}'");
            this.output.WriteLine(
                $"  RecoveryForensicsValidated='{(recoveryProof is null || recoveryProof.RecoveryForensicsIds.Count == recoveryProof.RecoveredExecutionIds.Count).ToString().ToLowerInvariant()}'");
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
            bool injectParentHostFailure)
        {
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
                $"ParentHostFailureInjected='{injectParentHostFailure.ToString().ToLowerInvariant()}'");
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
                $"RecoveryForensicsValidated='{(!injectParentHostFailure || totalRecoveryForensicsCount == totalRecoveredRunCount).ToString().ToLowerInvariant()}'");
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
            bool injectParentHostFailure)
        {
            this.output.WriteLine(string.Empty);
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} TIMING SUMMARY");
            this.output.WriteLine(
                $"  - Setup durable control plane, tenant MCP client, parent Process Hosts, and exact runtime topology: {setupDuration}");

            var drainTimingLabel =
                injectParentHostFailure
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
                        DelayMs = injectParentHostFailure ? 750 : 150,
                        FlakyStepInterval = 0,
                        EnableRetention = false
                    }
                };

            return template with
            {
                Name = string.Concat(
                    "process-host-pool-production-",
                    injectParentHostFailure ? "parent-failure" : "capacity",
                    "-cycles-",
                    executionCycleCount),
                ControlPlaneIdPrefix =
                    "production-process-host-pool",
                Tenants = new[] { tenant },
                SubmitMode = ProductionRuntimeSubmitMode.QueueFirst,
                AssertRetention = false,
                ScaleOutTimeout = TimeSpan.FromMinutes(5),
                DispatchTimeout = TimeSpan.FromMinutes(10),
                CompletionTimeout = injectParentHostFailure
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

        private static async Task<ProcessHostPoolProductionFailureTarget>
            WaitForBusyParentHostFailureTargetAsync(
                IAiRuntimeInstanceRegistry registry,
                IAiSharedRunStore sharedRunStore,
                IAiRuntimeRunExecutionIndex runExecutionIndex,
                ProcessHostPoolProductionCluster cluster,
                IReadOnlySet<string> submittedSharedRunIds,
                string controlPlaneId,
                string tenantId,
                TimeSpan timeout)
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
                    (await sharedRunStore
                            .ListAsync(
                                includeCancelled: true,
                                includeCompleted: true,
                                includeFailed: true)
                            .ConfigureAwait(false))
                        .Where(
                            run =>
                                submittedSharedRunIds.Contains(
                                    run.SharedRunId) &&
                                StringComparer.Ordinal.Equals(
                                    run.ControlPlaneId,
                                    controlPlaneId) &&
                                StringComparer.Ordinal.Equals(
                                    run.ExecutionContextSnapshot.TenantId,
                                    tenantId) &&
                                !string.IsNullOrWhiteSpace(
                                    run.AssignedRuntimeInstanceId) &&
                                !string.IsNullOrWhiteSpace(run.LocalRunId))
                        .ToArray();

                lastAssignedCount = sharedRuns.Length;
                lastActiveCount = 0;

                foreach (var host in cluster.Hosts.OrderBy(value => value.Ordinal))
                {
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
            IReadOnlyList<AiRuntimeInstanceSnapshot> topology)
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
            bool injectParentHostFailure)
        {
            this.output.WriteLine(
                $"# {this.profile.LogPrefix} PRODUCTION PROOF");
            this.output.WriteLine(
                injectParentHostFailure
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
                injectParentHostFailure
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
                $"  InjectParentHostFailure='{injectParentHostFailure}'");
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
