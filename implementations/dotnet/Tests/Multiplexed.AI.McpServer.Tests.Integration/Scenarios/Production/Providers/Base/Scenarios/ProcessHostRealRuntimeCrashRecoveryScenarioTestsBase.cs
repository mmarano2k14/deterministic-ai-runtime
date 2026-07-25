using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Stores;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Scenarios
{
    /// <summary>
    /// Base class for process-host runtime crash recovery scenario tests.
    /// </summary>
    public abstract class ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase
    {
        private const int StepCount = 100;
        private const int KillAfterCompletedStepCount = 25;
        private const int MultiTenantStepCount = 50;
        private const int FlakyStepIntervalMs = 500;
        private const int CrashCheckpointStateTtlMinutes = 30;
        private static readonly TimeSpan ScaleOutWatcherReadinessTimeout =
            TimeSpan.FromMinutes(1);

        private readonly ITestOutputHelper output;
        private readonly IProcessHostScenarioRuntimeProfile profile;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase"/> class
        /// using the historical durable polling observation mode.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The process-host scenario runtime profile.</param>
        protected ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase(
            ITestOutputHelper output,
            IProcessHostScenarioRuntimeProfile profile)
            : this(
                output,
                profile,
                ProductionRecoveryObservationMode.HybridSignals)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase"/> class
        /// using the requested recovery observation mode.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The process-host scenario runtime profile.</param>
        /// <param name="observationMode">The recovery observation mode.</param>
        /// <param name="hybridFallbackPollInterval">The durable fallback polling interval used in hybrid mode.</param>
        protected ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase(
            ITestOutputHelper output,
            IProcessHostScenarioRuntimeProfile profile,
            ProductionRecoveryObservationMode observationMode,
            TimeSpan? hybridFallbackPollInterval = null)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));

            ObservationMode = observationMode;
            HybridFallbackPollInterval = hybridFallbackPollInterval ?? TimeSpan.FromSeconds(2);

            if (HybridFallbackPollInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hybridFallbackPollInterval),
                    HybridFallbackPollInterval,
                    "The hybrid fallback polling interval must be greater than zero.");
            }
        }

        /// <summary>
        /// Gets how runtime progress and redispatch are observed.
        /// </summary>
        protected ProductionRecoveryObservationMode ObservationMode { get; }

        /// <summary>
        /// Gets the durable fallback interval used by hybrid observation.
        /// </summary>
        protected TimeSpan HybridFallbackPollInterval { get; }

        /// <summary>
        /// Verifies that two tenants can recover real process-host runtime crashes with strict DAG resume,
        /// forensics, replay, ledger, trace, inventory proof, and no cross-tenant recovery leak.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            var scenario =
                CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(3);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(7);

            var scenarioStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            var phaseStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            var timings =
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

            void WriteTiming(string phaseName)
            {
                var elapsed =
                    phaseStopwatch.Elapsed;

                timings[phaseName] = elapsed;

                output.WriteLine(
                    $"[{profile.LogPrefix} TWO-TENANT CRASH TIMING] Phase='{phaseName}', Duration='{elapsed}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");

                phaseStopwatch.Restart();
            }

            void WritePhaseHeader(
                int number,
                string title,
                string proof)
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"# PHASE {number}/8 - {title}");
                output.WriteLine(proof);
            }

            void WriteScenarioIntro(
                string currentControlPlaneId,
                string currentRuntimeHostAssemblyPath,
                string currentTenantAPipelinePrefix,
                string currentTenantBPipelinePrefix)
            {
                output.WriteLine($"# SCENARIO INTRO - {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST TWO-TENANT CRASH RECOVERY");
                output.WriteLine("Executive proof: this scenario kills one real external runtime process per tenant, recovers in-flight DAG work with strict resume, recovers volatile local queued work through durable redispatch, and proves forensics, replay, ledger, trace, and tenant isolation.");
                output.WriteLine(string.Empty);
                output.WriteLine("Scenario contract:");
                output.WriteLine("  - [ON] Real external runtime host processes are used; no fixture runtime is accepted for this scenario.");
                output.WriteLine("  - [ON] Two isolated tenants must each lose one unsafe runtime instance.");
                output.WriteLine("  - [ON] In-flight DAG executions must resume with the same durable execution id.");
                output.WriteLine("  - [ON] Local queued work must be recovered through durable shared-run redispatch.");
                output.WriteLine("  - [ON] Runtime recovery forensics must exist for every recovered work item.");
                output.WriteLine("  - [ON] Recovered executions must expose MCP replay, ledger, and trace evidence.");
                output.WriteLine("  - [ON] No cross-tenant leak, duplicate recovery, or self-redispatch is allowed.");
                output.WriteLine(string.Empty);
                output.WriteLine("Workload summary:");
                output.WriteLine($"  StepCount='{MultiTenantStepCount}'");
                output.WriteLine($"  KillAfterCompletedStepCount='{KillAfterCompletedStepCount}'");
                output.WriteLine($"  FlakyStepIntervalMs='{FlakyStepIntervalMs}'");
                output.WriteLine("  TenantCount='2'");
                output.WriteLine("  RunsPerTenant='3'");
                output.WriteLine("  SubmittedRuns='6'");
                output.WriteLine("  ExpectedRecoveredWork='6'");
                output.WriteLine("  ExpectedReplayValidatedExecutions='6'");
                output.WriteLine("  TotalValidatedExecutionFlows='12'");
                output.WriteLine(string.Empty);
                output.WriteLine("Runtime profile:");
                output.WriteLine($"  Provider='{profile.ProviderName}'");
                output.WriteLine($"  ProviderLabel='{profile.ProviderLabel}'");
                output.WriteLine($"  ControlPlaneId='{currentControlPlaneId}'");
                output.WriteLine($"  HostCreationMode='{profile.HostCreationMode}'");
                output.WriteLine($"  PersistenceProfile='{scenario.PersistenceProfile}'");
                output.WriteLine($"  ObservabilityProfile='{scenario.ObservabilityProfile}'");
                output.WriteLine($"  RuntimeHostAssemblyPath='{currentRuntimeHostAssemblyPath}'");
                output.WriteLine($"  ObservationMode='{ObservationMode}'");
                output.WriteLine($"  HybridFallbackPollInterval='{HybridFallbackPollInterval}'");
                output.WriteLine(string.Empty);
                output.WriteLine("Timeout budget:");
                output.WriteLine($"  ScaleOutTimeout: {scenario.ScaleOutTimeout}");
                output.WriteLine($"  DispatchTimeout: {scenario.DispatchTimeout}");
                output.WriteLine($"  CompletionTimeout: {scenario.CompletionTimeout}");
                output.WriteLine(string.Empty);
                output.WriteLine($"[{profile.LogPrefix} TWO-TENANT CRASH PROOF] Starting. ControlPlaneId='{currentControlPlaneId}', TenantAPipelinePrefix='{currentTenantAPipelinePrefix}', TenantBPipelinePrefix='{currentTenantBPipelinePrefix}', RuntimeHostAssemblyPath='{currentRuntimeHostAssemblyPath}'.");
            }

            void WriteTimingSummary()
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"# {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST TWO-TENANT CRASH TIMING SUMMARY");

                foreach (var timing in timings)
                {
                    output.WriteLine($"  - {timing.Key}: {timing.Value}");
                }

                output.WriteLine($"  - Total: {scenarioStopwatch.Elapsed}");
            }

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            settings["Tests:UseCapturingLedgerRecorder"] = "false";

            await using var host =
                new GenericMcpServerTestHost(settings);

            var signalSubscriber =
                ResolveSignalSubscriber(host.Services);

            var processControlSelector =
                host.Services.GetRequiredService<AiRuntimeHostProcessControlSelector>();

            var processControl =
                processControlSelector.GetRequired(this.profile.HostCreationMode);

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var forensicsQueryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var sharedQueue =
                host.Services.GetRequiredService<IAiSharedQueue>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(
                recoveryOptions);

            var tenantA =
                scenario.Tenants.Single(tenant =>
                    string.Equals(
                        tenant.TenantId,
                        "tenant-real-crash-a",
                        StringComparison.Ordinal));

            var tenantB =
                scenario.Tenants.Single(tenant =>
                    string.Equals(
                        tenant.TenantId,
                        "tenant-real-crash-b",
                        StringComparison.Ordinal));

            using var tenantAHttpClient =
                host.CreateClient();

            using var tenantBHttpClient =
                host.CreateClient();

            var tenantAMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantAHttpClient,
                        profile.RequestedBy,
                        tenantId: tenantA.TenantId,
                        tenantGroupId: tenantA.TenantGroupId)
                    .ConfigureAwait(false);

            var tenantBMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantBHttpClient,
                        profile.RequestedBy,
                        tenantId: tenantB.TenantId,
                        tenantGroupId: tenantB.TenantGroupId)
                    .ConfigureAwait(false);

            await WaitForScaleOutWatcherReadyAsync(
                    host.Services,
                    controlPlaneId)
                .ConfigureAwait(false);

            WriteTiming("Setup host services, tenant MCP clients, and scale-out watcher readiness");

            var ledgerTimelineFromUtc =
                DateTimeOffset.UtcNow.AddSeconds(-5);

            var tenantAPipelinePrefix =
                $"{scenario.Name}-{tenantA.TenantId}-real-crash-{Guid.NewGuid():N}";

            var tenantBPipelinePrefix =
                $"{scenario.Name}-{tenantB.TenantId}-real-crash-{Guid.NewGuid():N}";

            WriteScenarioIntro(
                controlPlaneId,
                runtimeHostAssemblyPath,
                tenantAPipelinePrefix,
                tenantBPipelinePrefix);

            WriteTiming("Scenario identifiers and intro output");

            WritePhaseHeader(
                1,
                "BUILD ASSIGNED WORK INVENTORY",
                "[PASS TARGET] Submit three runs per tenant and capture one in-flight execution plus local queued work on the failed runtime candidate.");

            var tenantAInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        tenantAMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantA,
                        controlPlaneId,
                        tenantAPipelinePrefix,
                        profile.RequestedBy,
                        profile.Source,
                        runCount: 3,
                        minimumInFlightExecutionCount: 1,
                        minimumLocalQueuedRunCount: 1,
                        minimumCompletedStepsBeforeKill: KillAfterCompletedStepCount,
                        scaleOutTimeout: scenario.ScaleOutTimeout,
                        dispatchTimeout: scenario.DispatchTimeout,
                        progressTimeout: TimeSpan.FromMinutes(3),
                        observationMode: ObservationMode);

            var tenantBInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        tenantBMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantB,
                        controlPlaneId,
                        tenantBPipelinePrefix,
                        profile.RequestedBy,
                        profile.Source,
                        runCount: 3,
                        minimumInFlightExecutionCount: 1,
                        minimumLocalQueuedRunCount: 1,
                        minimumCompletedStepsBeforeKill: KillAfterCompletedStepCount,
                        scaleOutTimeout: scenario.ScaleOutTimeout,
                        dispatchTimeout: scenario.DispatchTimeout,
                        progressTimeout: TimeSpan.FromMinutes(3),
                        observationMode: ObservationMode);

            await Task
                .WhenAll(
                    tenantAInventoryTask,
                    tenantBInventoryTask)
                .ConfigureAwait(false);

            var tenantAInventory =
                await tenantAInventoryTask.ConfigureAwait(false);

            var tenantBInventory =
                await tenantBInventoryTask.ConfigureAwait(false);

            WriteTiming("Build assigned work inventory for both tenants");

            Assert.NotEqual(
                tenantAInventory.RuntimeInstanceId,
                tenantBInventory.RuntimeInstanceId);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                tenantAInventory.RuntimeInstanceId,
                tenantA);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                tenantBInventory.RuntimeInstanceId,
                tenantB);

            WriteTiming("Validate selected failed runtime tenant ownership");

            WritePhaseHeader(
                2,
                "KILL REAL RUNTIME PROCESSES AND WAIT AUTOMATIC RECOVERY",
                "[PASS TARGET] Kill one real process per tenant, wait for unsafe detection, automatic requeue, replacement selection, and redispatch without manual reconciliation.");

            var tenantARecoveryTask =
            ProductionRealRuntimeCrashRecoveryTestHelpers
                .KillRuntimeAndRecoverAssignedInventoryAsync(
                    output,
                    processControl,
                    registry,
                    runExecutionIndex,
                    sharedRunStore,
                    sharedQueue,
                    dagStore,
                    tenantAInventory,
                    minimumCompletedStepsBeforeKill:
                        KillAfterCompletedStepCount,
                    progressTimeout:
                        TimeSpan.FromMinutes(3),
                    unsafeTimeout:
                        TimeSpan.FromSeconds(60),
                    requeueTimeout:
                        TimeSpan.FromSeconds(180),
                    redispatchTimeout:
                        scenario.DispatchTimeout,
                    executionResolveTimeout:
                        TimeSpan.FromSeconds(60),
                    observationMode:
                        ObservationMode,
                    signalSubscriber:
                        signalSubscriber,
                    controlPlaneId:
                        controlPlaneId,
                    hybridFallbackPollInterval:
                        HybridFallbackPollInterval);

            var tenantBRecoveryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                        output,
                        processControl,
                        registry,
                        runExecutionIndex,
                        sharedRunStore,
                        sharedQueue,
                        dagStore,
                        tenantBInventory,
                        minimumCompletedStepsBeforeKill:
                            KillAfterCompletedStepCount,
                        progressTimeout:
                            TimeSpan.FromMinutes(3),
                        unsafeTimeout:
                            TimeSpan.FromSeconds(60),
                        requeueTimeout:
                            TimeSpan.FromSeconds(180),
                        redispatchTimeout:
                            scenario.DispatchTimeout,
                        executionResolveTimeout:
                            TimeSpan.FromSeconds(60),
                        observationMode:
                            ObservationMode,
                        signalSubscriber:
                            signalSubscriber,
                        controlPlaneId:
                            controlPlaneId,
                        hybridFallbackPollInterval:
                            HybridFallbackPollInterval);

            await Task
                .WhenAll(
                    tenantARecoveryTask,
                    tenantBRecoveryTask)
                .ConfigureAwait(false);

            var tenantARecovery =
                await tenantARecoveryTask.ConfigureAwait(false);

            var tenantBRecovery =
                await tenantBRecoveryTask.ConfigureAwait(false);

            WriteTiming("Kill real runtime processes and wait for automatic recovery");

            WritePhaseHeader(
                3,
                "MCP RUNTIME RECOVERY FORENSICS PROOF",
                "[PASS TARGET] Every recovered work item must have runtime recovery forensics with no cross-tenant leak and no duplicate recovery record.");

            var recoveries =
                new[]
                {
            tenantARecovery,
            tenantBRecovery
                };

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH STEP 1 - MCP FORENSICS PROOF] Starting recovery forensics validation.");

            var tenantAForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        output,
                        forensicsQueryService,
                        tenantARecovery,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            var tenantBForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        output,
                        forensicsQueryService,
                        tenantBRecovery,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantRecoveryForensicsLeak(
                tenantARecovery,
                tenantAForensics,
                tenantBRecovery,
                tenantBForensics);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoDuplicateRecoveryForensics(
                tenantAForensics
                    .Concat(tenantBForensics)
                    .ToArray());

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH STEP 1 - MCP FORENSICS PROOF] " +
                $"TenantA='{tenantA.TenantId}', FailedRuntimeA='{tenantAInventory.RuntimeInstanceId}', ExpectedA='{tenantARecovery.RecoveredWorks.Count}', ActualA='{tenantAForensics.Count}', " +
                $"TenantB='{tenantB.TenantId}', FailedRuntimeB='{tenantBInventory.RuntimeInstanceId}', ExpectedB='{tenantBRecovery.RecoveredWorks.Count}', ActualB='{tenantBForensics.Count}', " +
                "CrossTenantLeakDetected='false', DuplicateRecoveryDetected='false'.");

            WriteTiming("Validate MCP runtime recovery forensics");

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantInventoryRecoveryLeak(
                recoveries);

            WriteTiming("Validate no cross-tenant inventory recovery leak");

            WritePhaseHeader(
                4,
                "RECOVERED DAG COMPLETION",
                "[PASS TARGET] All recovered DAG executions must complete the configured step count after strict resume or durable redispatch.");

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    output,
                    dagStore,
                    runExecutionIndex,
                    tenantARecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    output,
                    dagStore,
                    runExecutionIndex,
                    tenantBRecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            WriteTiming("Wait for recovered DAG completion");

            WritePhaseHeader(
                5,
                "TERMINAL RUNTIME RUN STATUS CONVERGENCE",
                "[PASS TARGET] MCP runtime queue status must converge to completed for all recovered local runs.");

            var tenantARedispatchedRuns =
                tenantARecovery.RecoveredWorks
                    .Select(work => work.RedispatchedRun)
                    .ToArray();

            var tenantBRedispatchedRuns =
                tenantBRecovery.RecoveredWorks
                    .Select(work => work.RedispatchedRun)
                    .ToArray();

            var tenantAFinalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        tenantAMcp,
                        tenantARedispatchedRuns,
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            var tenantBFinalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        tenantBMcp,
                        tenantBRedispatchedRuns,
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            AssertAllRuntimeStatusesCompleted(
                tenantAFinalStatuses);

            AssertAllRuntimeStatusesCompleted(
                tenantBFinalStatuses);

            WriteTiming("Wait for terminal runtime run statuses");

            WritePhaseHeader(
                6,
                "MCP REPLAY LEDGER TRACE PROOF",
                "[PASS TARGET] MCP replay tooling must expose replay report, replay ledger, replay trace, execution ledger, execution trace, completion evidence, and step-completion evidence.");

            var tenantAReplayProofs =
                await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                    .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                        tenantAMcp,
                        tenantA.TenantId,
                        tenantAFinalStatuses,
                        profile.RequestedBy,
                        profile.Source)
                    .ConfigureAwait(false);

            var tenantBReplayProofs =
                await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                    .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                        tenantBMcp,
                        tenantB.TenantId,
                        tenantBFinalStatuses,
                        profile.RequestedBy,
                        profile.Source)
                    .ConfigureAwait(false);

            ProductionRuntimeReplayOutput.WriteRecoveredExecutionReplayProof(
                output,
                tenantAReplayProofs,
                tenantBReplayProofs);

            WriteTiming("Validate MCP replay ledger and trace proof");

            WritePhaseHeader(
                7,
                "MCP TENANT-SCOPED LEDGER PROOF",
                "[PASS TARGET] Tenant-scoped MCP ledger queries must expose control-plane, runtime-instance, and recovery evidence for both tenants.");

            var ledgerTimelineToUtc =
                DateTimeOffset.UtcNow.AddSeconds(5);

            var tenantALedgerQuery =
                await ProductionControlPlaneLedgerTenantQuery
                    .QueryRecoveredTenantLedgerEvidenceAsync(
                        tenantAMcp,
                        tenantARecovery,
                        tenantAReplayProofs.Select(proof => proof.ExecutionId).ToArray(),
                        ledgerTimelineFromUtc,
                        ledgerTimelineToUtc)
                    .ConfigureAwait(false);

            var tenantBLedgerQuery =
                await ProductionControlPlaneLedgerTenantQuery
                    .QueryRecoveredTenantLedgerEvidenceAsync(
                        tenantBMcp,
                        tenantBRecovery,
                        tenantBReplayProofs.Select(proof => proof.ExecutionId).ToArray(),
                        ledgerTimelineFromUtc,
                        ledgerTimelineToUtc)
                    .ConfigureAwait(false);

            var tenantALedgerEntries =
                tenantALedgerQuery.Entries;

            var tenantBLedgerEntries =
                tenantBLedgerQuery.Entries;

            var ledgerEntries =
                tenantALedgerEntries
                    .Concat(tenantBLedgerEntries)
                    .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .Select(group => group.OrderBy(entry => entry.TimestampUtc).ThenBy(entry => entry.Sequence).First())
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.Sequence)
                    .ToArray();

            Assert.NotEmpty(
                ledgerEntries);

            Assert.Contains(
                ledgerEntries,
                entry =>
                    entry.EventType.StartsWith(
                        "control.",
                        StringComparison.Ordinal) ||
                    entry.EventType.Contains(
                        "runtime-execution-recovery",
                        StringComparison.Ordinal) ||
                    entry.EventType.Contains(
                        "runtime-instance",
                        StringComparison.Ordinal));

            Assert.Contains(
                ledgerEntries,
                entry =>
                    LedgerEntryContainsTenant(
                        entry,
                        tenantA.TenantId));

            Assert.Contains(
                ledgerEntries,
                entry =>
                    LedgerEntryContainsTenant(
                        entry,
                        tenantB.TenantId));

            var tenantBEntriesVisibleFromTenantA =
                tenantALedgerEntries.Count(entry =>
                    LedgerEntryContainsTenant(
                        entry,
                        tenantB.TenantId));

            var tenantAEntriesVisibleFromTenantB =
                tenantBLedgerEntries.Count(entry =>
                    LedgerEntryContainsTenant(
                        entry,
                        tenantA.TenantId));

            var crossTenantLedgerLeakDetected =
                tenantBEntriesVisibleFromTenantA > 0 ||
                tenantAEntriesVisibleFromTenantB > 0;

            var tenantAInfraEntries =
                tenantALedgerEntries.Count(IsInfraLedgerEntry);

            var tenantBInfraEntries =
                tenantBLedgerEntries.Count(IsInfraLedgerEntry);

            var infraLedgerValidated =
                tenantAInfraEntries > 0 &&
                tenantBInfraEntries > 0;

            Assert.False(
                crossTenantLedgerLeakDetected,
                $"Cross-tenant ledger leak detected. TenantBEntriesVisibleFromTenantA='{tenantBEntriesVisibleFromTenantA}', TenantAEntriesVisibleFromTenantB='{tenantAEntriesVisibleFromTenantB}'.");

            Assert.True(
                tenantAInfraEntries > 0,
                $"Tenant A scoped ledger query did not return infra/control-plane/runtime recovery evidence. TenantId='{tenantA.TenantId}', RuntimeIds='{string.Join(",", tenantALedgerQuery.RuntimeInstanceIds)}', ExecutionIds='{string.Join(",", tenantALedgerQuery.ExecutionIds)}'.");

            Assert.True(
                tenantBInfraEntries > 0,
                $"Tenant B scoped ledger query did not return infra/control-plane/runtime recovery evidence. TenantId='{tenantB.TenantId}', RuntimeIds='{string.Join(",", tenantBLedgerQuery.RuntimeInstanceIds)}', ExecutionIds='{string.Join(",", tenantBLedgerQuery.ExecutionIds)}'.");

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH STEP 3 - MCP LEDGER PROOF] " +
                $"TenantAEntries='{tenantALedgerEntries.Count}', TenantARuntimeIds='{tenantALedgerQuery.RuntimeInstanceIds.Count}', TenantAExecutionIds='{tenantALedgerQuery.ExecutionIds.Count}', TenantAInfraEntries='{tenantAInfraEntries}', TenantBEntriesVisibleFromTenantA='{tenantBEntriesVisibleFromTenantA}', " +
                $"TenantBEntries='{tenantBLedgerEntries.Count}', TenantBRuntimeIds='{tenantBLedgerQuery.RuntimeInstanceIds.Count}', TenantBExecutionIds='{tenantBLedgerQuery.ExecutionIds.Count}', TenantBInfraEntries='{tenantBInfraEntries}', TenantAEntriesVisibleFromTenantB='{tenantAEntriesVisibleFromTenantB}', " +
                $"CombinedScenarioEntries='{ledgerEntries.Length}', QueryScope='runtime-instance + execution', IncludesInfra='true', InfraLedgerValidated='{infraLedgerValidated.ToString().ToLowerInvariant()}', CrossTenantLedgerLeakDetected='{crossTenantLedgerLeakDetected.ToString().ToLowerInvariant()}', TimestampFromUtc='{ledgerTimelineFromUtc:O}', TimestampToUtc='{ledgerTimelineToUtc:O}'.");

            var expectedRecoveredWorkCount =
                6;

            var causalChainLedgerEntries =
                await ProductionControlPlaneLedgerCausalChainQuery
                    .QueryRecoveredScenarioCausalChainEvidenceAsync(
                        tenantAMcp,
                        controlPlaneId,
                        new[] { tenantA.TenantId, tenantB.TenantId },
                        new[] { tenantARecovery, tenantBRecovery },
                        tenantAReplayProofs.Concat(tenantBReplayProofs).Select(proof => proof.ExecutionId).ToArray(),
                        new[] { tenantAPipelinePrefix, tenantBPipelinePrefix },
                        ledgerTimelineFromUtc,
                        ledgerTimelineToUtc)
                    .ConfigureAwait(false);

            Assert.NotEmpty(
                causalChainLedgerEntries);

            var failedRuntimeUnsafeValidated =
                tenantARecovery.RecoveredWorks.Count == tenantAInventory.Works.Count &&
                tenantBRecovery.RecoveredWorks.Count == tenantBInventory.Works.Count;

            var causalChainProof =
                ProductionControlPlaneLedgerCausalChainProof.Validate(
                    causalChainLedgerEntries,
                    expectedRecoveredWorkCount,
                    tenantARecovery.RecoveredWorks.Count + tenantBRecovery.RecoveredWorks.Count,
                    failedRuntimeUnsafeValidated,
                    requireProcessRuntimeHostStarted:
                        this.profile.HostCreationMode ==
                        AiRuntimeHostCreationMode.Process);

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH MCP CONTROL-PLANE LEDGER QUERY PROOF] " +
                $"ScenarioCausalChainEntries='{causalChainLedgerEntries.Count}', ControlPlaneEntries='{causalChainLedgerEntries.Count(entry => entry.EventType.StartsWith("control.", StringComparison.Ordinal))}', QueryScope='runtime-instance ids + execution ids + control-plane run execution ids + scenario membership filter'.");

            ProductionControlPlaneLedgerCausalChainProofOutput.Write(
                output,
                controlPlaneId,
                tenantA.TenantId,
                tenantB.TenantId,
                tenantAInventory.RuntimeInstanceId,
                tenantBInventory.RuntimeInstanceId,
                causalChainProof,
                crossTenantLedgerLeakDetected,
                infraLedgerValidated);

            WriteTiming("Query and validate tenant scoped MCP ledger and control-plane causal chain");

            WritePhaseHeader(
                8,
                "FINAL PRODUCTION PROOF",
                "[PASS TARGET] Re-query final runtime recovery forensics after completion, print the causal forensics timeline first, then summarize recovery, replay, ledger, trace, timing, and safety invariants in one operator-readable line.");

            var tenantAFinalForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        output,
                        forensicsQueryService,
                        tenantARecovery,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            var tenantBFinalForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        output,
                        forensicsQueryService,
                        tenantBRecovery,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantRecoveryForensicsLeak(
                tenantARecovery,
                tenantAFinalForensics,
                tenantBRecovery,
                tenantBFinalForensics);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoDuplicateRecoveryForensics(
                tenantAFinalForensics
                    .Concat(tenantBFinalForensics)
                    .ToArray());

            output.WriteLine(string.Empty);
            output.WriteLine("# FINAL FORENSICS TIMELINE PROOF");
            output.WriteLine("Source: runtime recovery forensics queried after recovered DAG completion, terminal runtime status convergence, replay, ledger, and trace validation.");
            output.WriteLine(string.Empty);

            ProductionTenantRecoveryFinalProofHelper.WriteForensicsTimelineProof(
                output.WriteLine,
                tenantA.TenantId,
                tenantAFinalForensics,
                record => record.ForensicsId,
                record => record.ExecutionId,
                record => record.SharedRunId,
                record => record.TenantId,
                record => record.RuntimeFailureIncidentId,
                record => record.Timeline.Select(item => item.EventType).ToArray());

            output.WriteLine(string.Empty);

            ProductionTenantRecoveryFinalProofHelper.WriteForensicsTimelineProof(
                output.WriteLine,
                tenantB.TenantId,
                tenantBFinalForensics,
                record => record.ForensicsId,
                record => record.ExecutionId,
                record => record.SharedRunId,
                record => record.TenantId,
                record => record.RuntimeFailureIncidentId,
                record => record.Timeline.Select(item => item.EventType).ToArray());

            output.WriteLine(string.Empty);

            WriteTiming("Query final runtime recovery forensics timelines");

            var tenantAFinalRecoveryProof =
                ProductionTenantRecoveryFinalProofHelper.Build(
                    tenantA.TenantId,
                    tenantARecovery.RecoveredWorks,
                    tenantAFinalForensics,
                    work => work.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution,
                    record => record.RuntimeFailureIncidentId,
                    record => record.ForensicsId,
                    record => record.Timeline.Select(item => item.EventType).ToArray());

            var tenantBFinalRecoveryProof =
                ProductionTenantRecoveryFinalProofHelper.Build(
                    tenantB.TenantId,
                    tenantBRecovery.RecoveredWorks,
                    tenantBFinalForensics,
                    work => work.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution,
                    record => record.RuntimeFailureIncidentId,
                    record => record.ForensicsId,
                    record => record.Timeline.Select(item => item.EventType).ToArray());

            WriteTiming("Scenario finalization");

            WriteTimingSummary();

            output.WriteLine(string.Empty);

            ProductionTenantLedgerSummaryOutput.Write(
                output,
                "TENANT-SCOPED LEDGER SUMMARY",
                new[]
                {
            new ProductionTenantLedgerSummary(
                tenantA.TenantId,
                tenantALedgerQuery.RuntimeInstanceIds,
                tenantALedgerQuery.ExecutionIds,
                tenantALedgerEntries),
            new ProductionTenantLedgerSummary(
                tenantB.TenantId,
                tenantBLedgerQuery.RuntimeInstanceIds,
                tenantBLedgerQuery.ExecutionIds,
                tenantBLedgerEntries)
                },
                maxLedgerEntriesPerTenant: 50,
                maxEventTypeRowsPerTenant: 30,
                maxLedgerEntriesPerExecution: 25);

            output.WriteLine(string.Empty);

            output.WriteLine($"[{profile.LogPrefix} TWO-TENANT CRASH FINAL PROOF]");
            output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"TotalElapsed='{scenarioStopwatch.Elapsed}'");

            output.WriteLine(string.Empty);
            output.WriteLine("TenantA:");
            output.WriteLine($"  TenantId='{tenantA.TenantId}'");
            output.WriteLine($"  FailedRuntime='{tenantAInventory.RuntimeInstanceId}'");
            output.WriteLine($"  ForensicsTimelineTypes='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsTimelineTypes(tenantAFinalRecoveryProof)}'");
            output.WriteLine($"  RecoveryModes='{ProductionTenantRecoveryFinalProofHelper.FormatRecoveryModes(tenantAFinalRecoveryProof)}'");
            output.WriteLine($"  RuntimeFailureIncidentIds='{ProductionTenantRecoveryFinalProofHelper.FormatRuntimeFailureIncidentIds(tenantAFinalRecoveryProof)}'");
            output.WriteLine($"  ForensicsIds='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsIds(tenantAFinalRecoveryProof)}'");
            output.WriteLine($"  Recovered='{tenantARecovery.RecoveredWorks.Count}'");
            output.WriteLine($"  Forensics='{tenantAFinalForensics.Count}'");
            output.WriteLine($"  ReplayProof='{tenantAReplayProofs.Count}'");

            output.WriteLine(string.Empty);
            output.WriteLine("TenantB:");
            output.WriteLine($"  TenantId='{tenantB.TenantId}'");
            output.WriteLine($"  FailedRuntime='{tenantBInventory.RuntimeInstanceId}'");
            output.WriteLine($"  ForensicsTimelineTypes='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsTimelineTypes(tenantBFinalRecoveryProof)}'");
            output.WriteLine($"  RecoveryModes='{ProductionTenantRecoveryFinalProofHelper.FormatRecoveryModes(tenantBFinalRecoveryProof)}'");
            output.WriteLine($"  RuntimeFailureIncidentIds='{ProductionTenantRecoveryFinalProofHelper.FormatRuntimeFailureIncidentIds(tenantBFinalRecoveryProof)}'");
            output.WriteLine($"  ForensicsIds='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsIds(tenantBFinalRecoveryProof)}'");
            output.WriteLine($"  Recovered='{tenantBRecovery.RecoveredWorks.Count}'");
            output.WriteLine($"  Forensics='{tenantBFinalForensics.Count}'");
            output.WriteLine($"  ReplayProof='{tenantBReplayProofs.Count}'");

            output.WriteLine(string.Empty);
            output.WriteLine("Safety:");
            output.WriteLine("  ForensicsValidated='true'");
            output.WriteLine("  StrictResumeValidated='true'");
            output.WriteLine("  ReplayValidated='true'");
            output.WriteLine("  LedgerValidated='true'");
            output.WriteLine("  TraceValidated='true'");
            output.WriteLine($"  InfraLedgerValidated='{infraLedgerValidated.ToString().ToLowerInvariant()}'");
            output.WriteLine($"  ControlPlaneCausalChainValidated='{causalChainProof.IsValidated.ToString().ToLowerInvariant()}'");
            output.WriteLine("  CrossTenantLeakDetected='false'");
            output.WriteLine($"  CrossTenantLedgerLeakDetected='{crossTenantLedgerLeakDetected.ToString().ToLowerInvariant()}'");
            output.WriteLine("  DuplicateRecoveryDetected='false'");

            output.WriteLine(string.Empty);
        }

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight DAG execution resumes on a replacement runtime.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
        {
            var scenario =
                ProductionScenarioDefinitions.CreateRealRuntimeCrashRecoveryScenario(
                    stepCount: StepCount,
                    delayMs: 750,
                    includeSafeTenant: false,
                    completionTimeout: TimeSpan.FromMinutes(2));

            var tenant =
                scenario.Tenants.Single(current =>
                    string.Equals(current.TenantId, "tenant-concurrent-a", StringComparison.Ordinal));

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            await using var host =
                new GenericMcpServerTestHost(settings);

            var signalSubscriber =
                ResolveSignalSubscriber(host.Services);

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var processControlSelector =
                host.Services.GetRequiredService<AiRuntimeHostProcessControlSelector>();

            var processControl =
                processControlSelector.GetRequired(this.profile.HostCreationMode);

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var sharedQueue = host.Services.GetRequiredService<IAiSharedQueue>();

            using var httpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        httpClient,
                        profile.RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            await WaitForScaleOutWatcherReadyAsync(
                    host.Services,
                    controlPlaneId)
                .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-real-process-kill-{Guid.NewGuid():N}";

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Starting. Provider='{profile.ProviderName}', ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', Pipeline='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var dispatchedRun =
                await ProductionSharedRunTestHelpers
                    .SubmitAndDispatchOneRunAsync(
                        mcp,
                        scaleOutRequestStore,
                        tenant,
                        controlPlaneId,
                        pipelineName,
                        profile.RequestedBy,
                        profile.Source,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var resolvedExecution =
                await ProductionRecoveryWaitHelpers
                    .WaitForDurableDagExecutionAsync(
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        dispatchedRun.SharedRunId,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var dispatchedRunWithExecutionId =
                resolvedExecution.SharedRun;

            Assert.False(string.IsNullOrWhiteSpace(dispatchedRunWithExecutionId.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(dispatchedRunWithExecutionId.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(resolvedExecution.ExecutionId));

            var failedRuntimeInstanceId =
                dispatchedRunWithExecutionId.AssignedRuntimeInstanceId!;

            var localRunId =
                dispatchedRunWithExecutionId.LocalRunId!;

            var executionId =
                resolvedExecution.ExecutionId;

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Real run dispatched. SharedRunId='{dispatchedRunWithExecutionId.SharedRunId}', RuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}', SharedStoreExecutionId='{dispatchedRunWithExecutionId.ExecutionId}'.");

            await WaitForDagCompletedStepCountByObservationModeAsync(
                    dagStore,
                    signalSubscriber,
                    controlPlaneId,
                    executionId,
                    KillAfterCompletedStepCount,
                    TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Durable DAG progress reached. ExecutionId='{executionId}', CompletedSteps>='{KillAfterCompletedStepCount}'.");

            var killed =
                await processControl
                    .KillAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.True(
                killed,
                $"Runtime process was not killed. RuntimeInstanceId='{failedRuntimeInstanceId}'.");

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Runtime process killed. RuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForRuntimeInstanceUnsafeAsync(
                    registry,
                    failedRuntimeInstanceId,
                    TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Runtime instance automatically marked unsafe. RuntimeInstanceId='{failedRuntimeInstanceId}'.");

            var requeuedEntry =
                await ProductionRecoveryWaitHelpers
                    .WaitForRuntimeExecutionRequeuedAsync(
                        runExecutionIndex,
                        localRunId,
                        executionId,
                        TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] In-flight execution requeued for recovery. FailedRuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}', IndexStatus='{requeuedEntry.Status}', IndexRuntimeInstanceId='{requeuedEntry.RuntimeInstanceId}'.");

            var redispatchedRun =
             await ProductionRecoveryWaitHelpers
                 .WaitForRecoveredRunRedispatchedAsync(
                     sharedRunStore,
                     sharedQueue,
                     dispatchedRunWithExecutionId.SharedRunId,
                     failedRuntimeInstanceId,
                     localRunId,
                     TimeSpan.FromMinutes(2))
                 .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Recovered shared run redispatched. SharedRunId='{redispatchedRun.SharedRunId}', NewRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', NewLocalRunId='{redispatchedRun.LocalRunId}', OriginalRuntimeInstanceId='{failedRuntimeInstanceId}', OriginalLocalRunId='{localRunId}'.");

            var recoveredExecution =
                await ProductionRecoveryWaitHelpers
                    .WaitForDurableDagExecutionAsync(
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        redispatchedRun.SharedRunId,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            var recoveredExecutionId =
                recoveredExecution.ExecutionId;

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Recovered dispatch resolved durable execution. SharedRunId='{redispatchedRun.SharedRunId}', NewLocalRunId='{redispatchedRun.LocalRunId}', OriginalExecutionId='{executionId}', RecoveredExecutionId='{recoveredExecutionId}'.");

            Assert.False(string.IsNullOrWhiteSpace(recoveredExecutionId));
            Assert.Equal(executionId, recoveredExecutionId);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Strict DAG resume validated. Runtime process crash recovered on a replacement runtime while preserving the original durable execution id. " +
                $"OriginalExecutionId='{executionId}', RecoveredExecutionId='{recoveredExecutionId}', OriginalRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}'.");

            await WaitForDagCompletedStepCountByObservationModeAsync(
                    dagStore,
                    signalSubscriber,
                    controlPlaneId,
                    executionId,
                    StepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Recovered redispatch DAG execution completed all durable steps. RecoveredExecutionId='{recoveredExecutionId}', CompletedSteps='{StepCount}'.");
        }

        /// <summary>
        /// Verifies that all impacted tenants recover real process-host runtime crashes while all safe tenants
        /// continue normal execution without recovery, forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <param name="parallelism">
        /// The total parallel scenario count used to resolve harness-only timeout and crash-boundary budgets.
        /// When omitted, the strict single-scenario budget is preserved.
        /// </param>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace(
            int? parallelism = null)
        {
            var harnessBudget =
                CreateParallelScenarioHarnessBudget(
                    parallelism ?? 1);

            var scenario =
                CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario(
                    includeSafeTenant: true);

            scenario.ScaleOutTimeout =
                harnessBudget.ScaleOutTimeout;

            scenario.DispatchTimeout =
                harnessBudget.DispatchTimeout;

            scenario.CompletionTimeout =
                harnessBudget.CompletionTimeout;

            var scenarioStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            var phaseStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            var timings =
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

            void WriteTiming(string phaseName)
            {
                var elapsed =
                    phaseStopwatch.Elapsed;

                timings[phaseName] = elapsed;

                output.WriteLine(
                    $"[{profile.LogPrefix} MULTI-TENANT CRASH SAFE-TENANT TIMING] Phase='{phaseName}', Duration='{elapsed}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");

                phaseStopwatch.Restart();
            }

            void WritePhaseHeader(
                int number,
                string title,
                string proof)
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"# PHASE {number}/8 - {title}");
                output.WriteLine(proof);
            }

            void WriteTimingSummary()
            {
                output.WriteLine(string.Empty);
                output.WriteLine($"# {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST MULTI-TENANT CRASH SAFE-TENANT TIMING SUMMARY");

                foreach (var timing in timings)
                {
                    output.WriteLine($"  - {timing.Key}: {timing.Value}");
                }

                output.WriteLine($"  - Total: {scenarioStopwatch.Elapsed}");
            }

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                profile.BuildSettings(
                    scenario,
                    controlPlaneId,
                    runtimeHostAssemblyPath);

            if (settings.ContainsKey(
                    "AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"))
            {
                settings["AiGrpcRuntimeScaleOut:ReadinessTimeoutSeconds"] =
                    ((int)harnessBudget.RuntimeReadinessTimeout.TotalSeconds)
                        .ToString(CultureInfo.InvariantCulture);
            }

            settings["Tests:UseCapturingLedgerRecorder"] = "false";
            settings["AiRuntimeRecoveryForensics:StrictPersistence"] = "true";

            await using var host =
                new GenericMcpServerTestHost(settings);

            var signalSubscriber =
                ResolveSignalSubscriber(host.Services);

            var processControlSelector =
                host.Services.GetRequiredService<AiRuntimeHostProcessControlSelector>();

            var processControl =
                processControlSelector.GetRequired(this.profile.HostCreationMode);

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var forensicsQueryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            var sharedQueue = host.Services.GetRequiredService<IAiSharedQueue>();

            var crashCheckpointConnection =
                host.Services.GetRequiredService<IConnectionMultiplexer>();

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(
                recoveryOptions);

            var tenantHttpClients =
                new List<HttpClient>();

            try
            {
                var tenantContexts =
                    new List<ProcessHostTenantScenarioContext>();

                foreach (var tenant in scenario.Tenants)
                {
                    var httpClient =
                        host.CreateClient();

                    tenantHttpClients.Add(httpClient);

                    var mcp =
                        await McpRbacTestClientHelper
                            .CreateConfiguredClientAsync(
                                host,
                                httpClient,
                                profile.RequestedBy,
                                tenantId: tenant.TenantId,
                                tenantGroupId: tenant.TenantGroupId)
                            .ConfigureAwait(false);

                    var isSafe =
                        IsSafeCrashScenarioTenant(tenant);

                    tenantContexts.Add(
                        new ProcessHostTenantScenarioContext(
                            tenant,
                            mcp,
                            $"{scenario.Name}-{tenant.TenantId}-{(isSafe ? "safe" : "real-crash")}-{Guid.NewGuid():N}",
                            isSafe));
                }

                await WaitForScaleOutWatcherReadyAsync(
                        host.Services,
                        controlPlaneId)
                    .ConfigureAwait(false);

                var impactedContexts =
                    tenantContexts
                        .Where(context => !context.IsSafe)
                        .ToArray();

                var safeContexts =
                    tenantContexts
                        .Where(context => context.IsSafe)
                        .ToArray();

                Assert.NotEmpty(impactedContexts);
                Assert.NotEmpty(safeContexts);

                WriteTiming("Setup host services and tenant MCP clients");

                var ledgerTimelineFromUtc =
                    DateTimeOffset.UtcNow.AddSeconds(-5);

                output.WriteLine($"# SCENARIO INTRO - {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST MULTI-TENANT CRASH RECOVERY WITH SAFE TENANTS");
                output.WriteLine("Executive proof: every impacted tenant loses one real external runtime process and starts recovery immediately after its own crash inventory becomes ready, while every safe tenant continues normal execution without recovery contamination.");
                output.WriteLine(string.Empty);
                output.WriteLine("Scenario contract:");
                output.WriteLine("  - [ON] Real external runtime host processes are used; no fixture runtime is accepted for this scenario.");
                output.WriteLine("  - [ON] Each impacted tenant must lose one unsafe runtime instance without waiting for other tenant inventories.");
                output.WriteLine("  - [ON] Safe tenant runtimes must not be killed, redispatched for recovery, or receive recovery forensics.");
                output.WriteLine("  - [ON] Impacted in-flight DAG executions must resume with the same durable execution id.");
                output.WriteLine("  - [ON] Impacted local queued work must be recovered through durable shared-run redispatch.");
                output.WriteLine("  - [ON] No cross-tenant leak, duplicate recovery, or safe-tenant recovery contamination is allowed.");
                output.WriteLine(string.Empty);
                output.WriteLine("Workload summary:");
                output.WriteLine($"  StepCount='{MultiTenantStepCount}'");
                output.WriteLine($"  KillAfterCompletedStepCount='{harnessBudget.KillAfterCompletedStepCount}'");
                output.WriteLine($"  FlakyStepIntervalMs='{FlakyStepIntervalMs}'");
                output.WriteLine($"  TenantCount='{tenantContexts.Count}'");
                output.WriteLine($"  ImpactedTenantCount='{impactedContexts.Length}'");
                output.WriteLine($"  SafeTenantCount='{safeContexts.Length}'");
                output.WriteLine($"  SubmittedRuns='{tenantContexts.Sum(context => context.Tenant.Run.RunCount)}'");
                output.WriteLine($"  ExpectedRecoveredWork='{impactedContexts.Sum(context => context.Tenant.Run.RunCount)}'");
                output.WriteLine($"  ExpectedSafeTenantRecoveredWork='0'");
                output.WriteLine(string.Empty);
                output.WriteLine("Runtime profile:");
                output.WriteLine($"  Provider='{profile.ProviderName}'");
                output.WriteLine($"  ProviderLabel='{profile.ProviderLabel}'");
                output.WriteLine($"  ControlPlaneId='{controlPlaneId}'");
                output.WriteLine($"  HostCreationMode='{profile.HostCreationMode}'");
                output.WriteLine($"  PersistenceProfile='{scenario.PersistenceProfile}'");
                output.WriteLine($"  ObservabilityProfile='{scenario.ObservabilityProfile}'");
                output.WriteLine($"  RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'");
                output.WriteLine($"  ObservationMode='{ObservationMode}'");
                output.WriteLine($"  HybridFallbackPollInterval='{HybridFallbackPollInterval}'");
                output.WriteLine(string.Empty);
                output.WriteLine("Parallel harness budget:");
                output.WriteLine($"  Parallelism='{harnessBudget.Parallelism}'");
                output.WriteLine($"  PressureStepCount='{harnessBudget.PressureStepCount}'");
                output.WriteLine($"  ScaleOutTimeout='{harnessBudget.ScaleOutTimeout}'");
                output.WriteLine($"  RuntimeReadinessTimeout='{harnessBudget.RuntimeReadinessTimeout}'");
                output.WriteLine($"  DispatchTimeout='{harnessBudget.DispatchTimeout}'");
                output.WriteLine($"  RecoveryRedispatchTimeout='{harnessBudget.RecoveryRedispatchTimeout}'");
                output.WriteLine($"  ProgressTimeout='{harnessBudget.ProgressTimeout}'");
                output.WriteLine($"  UnsafeTimeout='{harnessBudget.UnsafeTimeout}'");
                output.WriteLine($"  RequeueTimeout='{harnessBudget.RequeueTimeout}'");
                output.WriteLine($"  ExecutionResolveTimeout='{harnessBudget.ExecutionResolveTimeout}'");
                output.WriteLine($"  CompletionTimeout='{harnessBudget.CompletionTimeout}'");
                output.WriteLine(string.Empty);

                foreach (var context in tenantContexts)
                {
                    output.WriteLine(
                        $"[{profile.LogPrefix} TENANT SCENARIO] TenantId='{context.Tenant.TenantId}', Role='{(context.IsSafe ? "Safe" : "Impacted")}', PipelinePrefix='{context.PipelinePrefix}', RunCount='{context.Tenant.Run.RunCount}'.");
                }

                WriteTiming("Scenario identifiers and intro output");

                WritePhaseHeader(
                    1,
                    "BUILD INVENTORIES AND CRASH IMPACTED TENANTS IMMEDIATELY",
                    "[PASS TARGET] Build every tenant inventory concurrently. As soon as an impacted tenant reaches the required in-flight/local-queued shape, kill that tenant runtime immediately instead of waiting at a global inventory barrier.");

                async Task<ProcessHostImpactedTenantExecutionResult> ExecuteImpactedTenantRecoveryFlowAsync(
                    ProcessHostTenantScenarioContext context)
                {
                    var crashCheckpointGate =
                        await ProductionCrashCheckpointGate
                            .ArmAsync(
                                crashCheckpointConnection,
                                output,
                                controlPlaneId,
                                context.Tenant.TenantId,
                                context.PipelinePrefix,
                                harnessBudget.KillAfterCompletedStepCount + 1,
                                TimeSpan.FromMinutes(CrashCheckpointStateTtlMinutes))
                            .ConfigureAwait(false);

                    try
                    {
                        var inventory =
                            await ProductionRealRuntimeCrashRecoveryTestHelpers
                                .SubmitAndBuildAssignedWorkInventoryAsync(
                                    output,
                                    context.Mcp,
                                    scaleOutRequestStore,
                                    sharedRunStore,
                                    sharedQueue,
                                    runExecutionIndex,
                                    dagStore,
                                    context.Tenant,
                                    controlPlaneId,
                                    context.PipelinePrefix,
                                    profile.RequestedBy,
                                    profile.Source,
                                    runCount: context.Tenant.Run.RunCount,
                                    minimumInFlightExecutionCount: 1,
                                    minimumLocalQueuedRunCount:
                                        context.Tenant.Run.RunCount - 1,
                                    minimumCompletedStepsBeforeKill:
                                        harnessBudget.KillAfterCompletedStepCount,
                                    scaleOutTimeout:
                                        scenario.ScaleOutTimeout,
                                    dispatchTimeout:
                                        scenario.DispatchTimeout,
                                    progressTimeout:
                                        harnessBudget.ProgressTimeout,
                                    observationMode:
                                        ObservationMode,
                                    crashCheckpointGate:
                                        crashCheckpointGate)
                                .ConfigureAwait(false);

                        ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                            inventory.RuntimeInstanceId,
                            context.Tenant);

                        output.WriteLine(
                            $"[{profile.LogPrefix} IMPACTED TENANT READY FOR CRASH] TenantId='{context.Tenant.TenantId}', RuntimeInstanceId='{inventory.RuntimeInstanceId}', InFlight='{inventory.InFlightExecutions.Count}', LocalQueued='{inventory.LocalQueuedRuns.Count}'. Killing immediately without waiting for other tenant inventories.");

                        var recovery =
                            await ProductionRealRuntimeCrashRecoveryTestHelpers
                                .KillRuntimeAndRecoverAssignedInventoryAsync(
                                    output,
                                    processControl,
                                    registry,
                                    runExecutionIndex,
                                    sharedRunStore,
                                    sharedQueue,
                                    dagStore,
                                    inventory,
                                    minimumCompletedStepsBeforeKill:
                                        harnessBudget.KillAfterCompletedStepCount,
                                    progressTimeout:
                                        harnessBudget.ProgressTimeout,
                                    unsafeTimeout:
                                        harnessBudget.UnsafeTimeout,
                                    requeueTimeout:
                                        harnessBudget.RequeueTimeout,
                                    redispatchTimeout:
                                        harnessBudget.RecoveryRedispatchTimeout,
                                    executionResolveTimeout:
                                        harnessBudget.ExecutionResolveTimeout,
                                    observationMode:
                                        ObservationMode,
                                    signalSubscriber:
                                        signalSubscriber,
                                    controlPlaneId:
                                        controlPlaneId,
                                    hybridFallbackPollInterval:
                                        HybridFallbackPollInterval,
                                    crashCheckpointGate:
                                        crashCheckpointGate)
                                .ConfigureAwait(false);

                        return new ProcessHostImpactedTenantExecutionResult(
                            context,
                            inventory,
                            recovery);
                    }
                    finally
                    {
                        await crashCheckpointGate
                            .ReleaseAsync()
                            .ConfigureAwait(false);
                    }
                }

                async Task<ProcessHostSafeTenantExecutionResult> BuildSafeTenantInventoryAsync(
                    ProcessHostTenantScenarioContext context)
                {
                    var crashCheckpointGate =
                        await ProductionCrashCheckpointGate
                            .ArmAsync(
                                crashCheckpointConnection,
                                output,
                                controlPlaneId,
                                context.Tenant.TenantId,
                                context.PipelinePrefix,
                                harnessBudget.KillAfterCompletedStepCount + 1,
                                TimeSpan.FromMinutes(CrashCheckpointStateTtlMinutes))
                            .ConfigureAwait(false);

                    try
                    {
                        var inventory =
                            await ProductionRealRuntimeCrashRecoveryTestHelpers
                                .SubmitAndBuildAssignedWorkInventoryAsync(
                                    output,
                                    context.Mcp,
                                    scaleOutRequestStore,
                                    sharedRunStore,
                                    sharedQueue,
                                    runExecutionIndex,
                                    dagStore,
                                    context.Tenant,
                                    controlPlaneId,
                                    context.PipelinePrefix,
                                    profile.RequestedBy,
                                    profile.Source,
                                    runCount: context.Tenant.Run.RunCount,
                                    minimumInFlightExecutionCount: 1,
                                    minimumLocalQueuedRunCount:
                                        context.Tenant.Run.RunCount - 1,
                                    minimumCompletedStepsBeforeKill:
                                        harnessBudget.KillAfterCompletedStepCount,
                                    scaleOutTimeout:
                                        scenario.ScaleOutTimeout,
                                    dispatchTimeout:
                                        scenario.DispatchTimeout,
                                    progressTimeout:
                                        harnessBudget.ProgressTimeout,
                                    observationMode:
                                        ObservationMode,
                                    crashCheckpointGate:
                                        crashCheckpointGate)
                                .ConfigureAwait(false);

                        ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                            inventory.RuntimeInstanceId,
                            context.Tenant);

                        return new ProcessHostSafeTenantExecutionResult(
                            context,
                            inventory);
                    }
                    finally
                    {
                        await crashCheckpointGate
                            .ReleaseAsync()
                            .ConfigureAwait(false);
                    }
                }

                var impactedFlowTasks =
                    impactedContexts
                        .Select(ExecuteImpactedTenantRecoveryFlowAsync)
                        .ToArray();

                var safeInventoryTasks =
                    safeContexts
                        .Select(BuildSafeTenantInventoryAsync)
                        .ToArray();

                var allTenantFlowTasks =
                    impactedFlowTasks
                        .Select(task => (Task)task)
                        .Concat(safeInventoryTasks.Select(task => (Task)task))
                        .ToArray();

                WritePhaseHeader(
                    2,
                    "KILL IMPACTED RUNTIMES AND WAIT AUTOMATIC RECOVERY",
                    "[PASS TARGET] Each impacted tenant flow kills its selected runtime immediately after its own inventory is ready, while safe tenant flows continue without process termination.");

                await Task
                    .WhenAll(allTenantFlowTasks)
                    .ConfigureAwait(false);

                var impactedResults =
                    await Task
                        .WhenAll(impactedFlowTasks)
                        .ConfigureAwait(false);

                var safeResults =
                    await Task
                        .WhenAll(safeInventoryTasks)
                        .ConfigureAwait(false);

                WriteTiming("Build tenant inventories, kill impacted runtimes, and wait for automatic recovery");

                var allRuntimeIds =
                    impactedResults
                        .Select(result => result.Inventory.RuntimeInstanceId)
                        .Concat(safeResults.Select(result => result.Inventory.RuntimeInstanceId))
                        .ToArray();

                Assert.Equal(
                    allRuntimeIds.Length,
                    allRuntimeIds.Distinct(StringComparer.Ordinal).Count());

                var recoveries =
                    impactedResults
                        .Select(result => result.Recovery)
                        .ToArray();

                ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantInventoryRecoveryLeak(
                    recoveries);

                var safeRuntimeIds =
                    safeResults
                        .Select(result => result.Inventory.RuntimeInstanceId)
                        .ToHashSet(StringComparer.Ordinal);

                var safeSharedRunIds =
                    safeResults
                        .SelectMany(result => result.Inventory.Works)
                        .Select(work => work.SharedRunId)
                        .ToHashSet(StringComparer.Ordinal);

                Assert.DoesNotContain(
                    recoveries.SelectMany(recovery => recovery.RecoveredWorks),
                    work =>
                        safeRuntimeIds.Contains(work.ReplacementRuntimeInstanceId) ||
                        safeSharedRunIds.Contains(work.Original.SharedRunId) ||
                        safeSharedRunIds.Contains(work.RedispatchedRun.SharedRunId));

                WriteTiming("Validate runtime ownership and no cross-tenant inventory recovery leak");

                WritePhaseHeader(
                    3,
                    "MCP RUNTIME RECOVERY FORENSICS PROOF",
                    "[PASS TARGET] Every impacted recovered work item must have tenant-owned recovery forensics, while safe tenants remain absent from recovery evidence.");

                var initialForensicsTasks =
                    impactedResults
                        .Select(async result => new
                        {
                            Result = result,
                            Records = await ProductionRealRuntimeCrashRecoveryTestHelpers
                                .AssertRecoveredInventoryForensicsAsync(
                                    output,
                                    forensicsQueryService,
                                    result.Recovery,
                                    TimeSpan.FromSeconds(60))
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var initialForensicsResults =
                    await Task
                        .WhenAll(initialForensicsTasks)
                        .ConfigureAwait(false);

                var initialForensicsRecords =
                    initialForensicsResults
                        .SelectMany(result => result.Records)
                        .ToArray();

                ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoDuplicateRecoveryForensics(
                    initialForensicsRecords);

                for (var leftIndex = 0; leftIndex < initialForensicsResults.Length; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < initialForensicsResults.Length; rightIndex++)
                    {
                        var left = initialForensicsResults[leftIndex];
                        var right = initialForensicsResults[rightIndex];

                        ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantRecoveryForensicsLeak(
                            left.Result.Recovery,
                            left.Records,
                            right.Result.Recovery,
                            right.Records);
                    }
                }

                var safeTenantIds =
                    safeContexts
                        .Select(context => context.Tenant.TenantId)
                        .ToHashSet(StringComparer.Ordinal);

                Assert.DoesNotContain(
                    initialForensicsRecords,
                    record => !string.IsNullOrWhiteSpace(record.TenantId) && safeTenantIds.Contains(record.TenantId));

                WriteTiming("Validate MCP runtime recovery forensics");

                WritePhaseHeader(
                    4,
                    "RECOVERED AND SAFE TENANT DAG COMPLETION",
                    "[PASS TARGET] Every impacted recovered DAG and every safe tenant normal DAG must complete the configured step count.");

                await Task
                    .WhenAll(
                        impactedResults.Select(result =>
                            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRecoveredInventoryDagCompletedAsync(
                                output,
                                dagStore,
                                runExecutionIndex,
                                result.Recovery,
                                MultiTenantStepCount,
                                scenario.CompletionTimeout)))
                    .ConfigureAwait(false);

                var safeExecutionTasks =
                    safeResults
                        .SelectMany(result =>
                            result.Inventory.Works.Select(async work => new
                            {
                                Result = result,
                                Execution = await ProductionRecoveryWaitHelpers
                                    .WaitForDurableDagExecutionAsync(
                                        sharedRunStore,
                                        runExecutionIndex,
                                        dagStore,
                                        work.SharedRunId,
                                        scenario.CompletionTimeout)
                                    .ConfigureAwait(false)
                            }))
                        .ToArray();

                var safeExecutions =
                    await Task
                        .WhenAll(safeExecutionTasks)
                        .ConfigureAwait(false);

                await Task
                    .WhenAll(
                        safeExecutions.Select(async item =>
                        {
                            await WaitForDagCompletedStepCountByObservationModeAsync(
                                    dagStore,
                                    signalSubscriber,
                                    controlPlaneId,
                                    item.Execution.ExecutionId,
                                    MultiTenantStepCount,
                                    scenario.CompletionTimeout)
                                .ConfigureAwait(false);

                            output.WriteLine(
                                $"[{profile.LogPrefix} SAFE TENANT COMPLETION] TenantId='{item.Result.Context.Tenant.TenantId}', SharedRunId='{item.Execution.SharedRun.SharedRunId}', RuntimeInstanceId='{item.Execution.SharedRun.AssignedRuntimeInstanceId}', LocalRunId='{item.Execution.SharedRun.LocalRunId}', ExecutionId='{item.Execution.ExecutionId}', CompletedSteps='{MultiTenantStepCount}'.");
                        }))
                    .ConfigureAwait(false);

                WriteTiming("Wait for recovered and safe tenant DAG completion");

                WritePhaseHeader(
                    5,
                    "TERMINAL RUNTIME RUN STATUS CONVERGENCE",
                    "[PASS TARGET] Runtime queue status must converge to completed for every impacted recovered run and every safe tenant normal run.");

                var impactedStatusTasks =
                    impactedResults
                        .Select(async result => new
                        {
                            Result = result,
                            Statuses = await McpTestWaitHelpers
                                .WaitForTerminalRuntimeRunStatusesAsync(
                                    result.Context.Mcp,
                                    result.Recovery.RecoveredWorks.Select(work => work.RedispatchedRun).ToArray(),
                                    timeout: scenario.CompletionTimeout)
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var impactedStatusResults =
                    await Task
                        .WhenAll(impactedStatusTasks)
                        .ConfigureAwait(false);

                var safeStatusTasks =
                    safeResults
                        .Select(async result =>
                        {
                            var executions =
                                safeExecutions
                                    .Where(item => string.Equals(
                                        item.Result.Context.Tenant.TenantId,
                                        result.Context.Tenant.TenantId,
                                        StringComparison.Ordinal))
                                    .Select(item => item.Execution)
                                    .ToArray();

                            var statuses =
                                await McpTestWaitHelpers
                                    .WaitForTerminalRuntimeRunStatusesAsync(
                                        result.Context.Mcp,
                                        executions.Select(execution => execution.SharedRun).ToArray(),
                                        timeout: scenario.CompletionTimeout)
                                    .ConfigureAwait(false);

                            return new
                            {
                                Result = result,
                                Executions = executions,
                                Statuses = statuses
                            };
                        })
                        .ToArray();

                var safeStatusResults =
                    await Task
                        .WhenAll(safeStatusTasks)
                        .ConfigureAwait(false);

                foreach (var result in impactedStatusResults)
                {
                    AssertAllRuntimeStatusesCompleted(result.Statuses);
                }

                foreach (var result in safeStatusResults)
                {
                    AssertAllRuntimeStatusesCompleted(result.Statuses);

                    Assert.DoesNotContain(
                        result.Statuses,
                        status => impactedResults.Any(impacted =>
                            string.Equals(
                                status.RuntimeInstanceId,
                                impacted.Inventory.RuntimeInstanceId,
                                StringComparison.Ordinal)));
                }

                WriteTiming("Wait for terminal runtime run statuses");

                WritePhaseHeader(
                    6,
                    "MCP REPLAY LEDGER TRACE PROOF",
                    "[PASS TARGET] MCP replay tooling must expose replay, ledger, trace, completion, and step-completion evidence for every impacted and safe tenant execution.");

                var impactedReplayTasks =
                    impactedStatusResults
                        .Select(async statusResult => new
                        {
                            Result = statusResult.Result,
                            Proofs = await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                                .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                                    statusResult.Result.Context.Mcp,
                                    statusResult.Result.Context.Tenant.TenantId,
                                    statusResult.Statuses,
                                    profile.RequestedBy,
                                    profile.Source)
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var safeReplayTasks =
                    safeStatusResults
                        .Select(async statusResult => new
                        {
                            Result = statusResult.Result,
                            Proofs = await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                                .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                                    statusResult.Result.Context.Mcp,
                                    statusResult.Result.Context.Tenant.TenantId,
                                    statusResult.Statuses,
                                    profile.RequestedBy,
                                    profile.Source)
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var allReplayTasks =
                    impactedReplayTasks
                        .Select(task => (Task)task)
                        .Concat(safeReplayTasks.Select(task => (Task)task))
                        .ToArray();

                /*
                 * Drain every replay request before the scenario host can be disposed.
                 * Awaiting impacted and safe batches sequentially is unsafe: if the
                 * first batch faults, the second batch is still running against the
                 * same TestServer while the method unwinds and disposes its services.
                 */
                await Task
                    .WhenAll(allReplayTasks)
                    .ConfigureAwait(false);

                output.WriteLine(
                    $"[REAL RUNTIME HARNESS TASK DRAIN] " +
                    $"TaskGroup='tenant-replay-requests', " +
                    $"TaskCount='{allReplayTasks.Length}', " +
                    "PendingAfterDrain='0'.");

                var impactedReplayResults =
                    await Task
                        .WhenAll(impactedReplayTasks)
                        .ConfigureAwait(false);

                var safeReplayResults =
                    await Task
                        .WhenAll(safeReplayTasks)
                        .ConfigureAwait(false);

                foreach (var replayResult in impactedReplayResults)
                {
                    output.WriteLine(
                        $"[{profile.LogPrefix} TENANT REPLAY PROOF] TenantId='{replayResult.Result.Context.Tenant.TenantId}', Role='Impacted', ReplayProofCount='{replayResult.Proofs.Count}', ExecutionIds='{string.Join(",", replayResult.Proofs.Select(proof => proof.ExecutionId))}'.");
                }

                foreach (var replayResult in safeReplayResults)
                {
                    output.WriteLine(
                        $"[{profile.LogPrefix} TENANT REPLAY PROOF] TenantId='{replayResult.Result.Context.Tenant.TenantId}', Role='Safe', ReplayProofCount='{replayResult.Proofs.Count}', ExecutionIds='{string.Join(",", replayResult.Proofs.Select(proof => proof.ExecutionId))}'.");
                }

                WriteTiming("Validate MCP replay ledger and trace proof");

                WritePhaseHeader(
                    7,
                    "MCP TENANT-SCOPED LEDGER PROOF",
                    "[PASS TARGET] Tenant-scoped ledger queries must expose recovery evidence for every impacted tenant, with no impacted-to-impacted or safe-tenant recovery leakage.");

                var ledgerTimelineToUtc =
                    DateTimeOffset.UtcNow.AddSeconds(5);

                var impactedLedgerTasks =
                    impactedReplayResults
                        .Select(async replayResult => new
                        {
                            Result = replayResult.Result,
                            ReplayProofs = replayResult.Proofs,
                            Query = await ProductionControlPlaneLedgerTenantQuery
                                .QueryRecoveredTenantLedgerEvidenceAsync(
                                    replayResult.Result.Context.Mcp,
                                    replayResult.Result.Recovery,
                                    replayResult.Proofs.Select(proof => proof.ExecutionId).ToArray(),
                                    ledgerTimelineFromUtc,
                                    ledgerTimelineToUtc)
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var impactedLedgerResults =
                    await Task
                        .WhenAll(impactedLedgerTasks)
                        .ConfigureAwait(false);

                var ledgerEntries =
                    impactedLedgerResults
                        .SelectMany(result => result.Query.Entries)
                        .GroupBy(entry => entry.EntryId, StringComparer.Ordinal)
                        .Select(group => group.OrderBy(entry => entry.TimestampUtc).ThenBy(entry => entry.Sequence).First())
                        .OrderBy(entry => entry.TimestampUtc)
                        .ThenBy(entry => entry.Sequence)
                        .ToArray();

                Assert.NotEmpty(ledgerEntries);

                var crossTenantLedgerLeakDetected =
                    false;

                foreach (var ledgerResult in impactedLedgerResults)
                {
                    var tenantId =
                        ledgerResult.Result.Context.Tenant.TenantId;

                    Assert.Contains(
                        ledgerResult.Query.Entries,
                        entry => LedgerEntryContainsTenant(entry, tenantId));

                    var foreignImpactedEntries =
                        impactedContexts
                            .Where(context => !string.Equals(context.Tenant.TenantId, tenantId, StringComparison.Ordinal))
                            .Sum(context => ledgerResult.Query.Entries.Count(entry =>
                                LedgerEntryContainsTenant(entry, context.Tenant.TenantId)));

                    var safeRecoveryEntries =
                        safeContexts.Sum(context => ledgerResult.Query.Entries.Count(entry =>
                            LedgerEntryContainsTenant(entry, context.Tenant.TenantId) &&
                            IsInfraLedgerEntry(entry) &&
                            entry.EventType.Contains("recovery", StringComparison.Ordinal)));

                    var infraEntryCount =
                        ledgerResult.Query.Entries.Count(IsInfraLedgerEntry);

                    Assert.True(
                        infraEntryCount > 0,
                        $"Tenant-scoped ledger query did not return infra/control-plane/runtime recovery evidence. TenantId='{tenantId}', RuntimeIds='{string.Join(",", ledgerResult.Query.RuntimeInstanceIds)}', ExecutionIds='{string.Join(",", ledgerResult.Query.ExecutionIds)}'.");

                    crossTenantLedgerLeakDetected |=
                        foreignImpactedEntries > 0 ||
                        safeRecoveryEntries > 0;

                    output.WriteLine(
                        $"[{profile.LogPrefix} TENANT-SCOPED LEDGER PROOF] TenantId='{tenantId}', Entries='{ledgerResult.Query.Entries.Count}', RuntimeIds='{ledgerResult.Query.RuntimeInstanceIds.Count}', ExecutionIds='{ledgerResult.Query.ExecutionIds.Count}', InfraEntries='{infraEntryCount}', ForeignImpactedEntries='{foreignImpactedEntries}', SafeRecoveryEntries='{safeRecoveryEntries}'.");
                }

                var infraLedgerValidated =
                    impactedLedgerResults.All(result =>
                        result.Query.Entries.Count(IsInfraLedgerEntry) > 0);

                Assert.False(
                    crossTenantLedgerLeakDetected,
                    "Cross-tenant or safe-tenant recovery ledger leakage was detected.");

                var expectedRecoveredWorkCount =
                    impactedResults.Sum(result => result.Inventory.Works.Count);

                var allImpactedReplayExecutionIds =
                    impactedReplayResults
                        .SelectMany(result => result.Proofs)
                        .Select(proof => proof.ExecutionId)
                        .ToArray();

                var causalChainLedgerEntries =
                    await ProductionControlPlaneLedgerCausalChainQuery
                        .QueryRecoveredScenarioCausalChainEvidenceAsync(
                            impactedResults[0].Context.Mcp,
                            controlPlaneId,
                            impactedResults.Select(result => result.Context.Tenant.TenantId).ToArray(),
                            recoveries,
                            allImpactedReplayExecutionIds,
                            impactedResults.Select(result => result.Context.PipelinePrefix).ToArray(),
                            ledgerTimelineFromUtc,
                            ledgerTimelineToUtc)
                        .ConfigureAwait(false);

                Assert.NotEmpty(causalChainLedgerEntries);

                var failedRuntimeUnsafeValidated =
                    impactedResults.All(result =>
                        result.Recovery.RecoveredWorks.Count == result.Inventory.Works.Count);

                var causalChainProof =
                    ProductionControlPlaneLedgerCausalChainProof.Validate(
                        causalChainLedgerEntries,
                        expectedRecoveredWorkCount,
                        recoveries.Sum(recovery => recovery.RecoveredWorks.Count),
                        failedRuntimeUnsafeValidated,
                        requireProcessRuntimeHostStarted:
                            this.profile.HostCreationMode ==
                            AiRuntimeHostCreationMode.Process);

                output.WriteLine(
                    $"[{profile.LogPrefix} MULTI-TENANT CONTROL-PLANE LEDGER QUERY PROOF] ScenarioCausalChainEntries='{causalChainLedgerEntries.Count}', ImpactedTenantCount='{impactedResults.Length}', ExpectedRecoveredWork='{expectedRecoveredWorkCount}', ActualRecoveredWork='{recoveries.Sum(recovery => recovery.RecoveredWorks.Count)}', Validated='{causalChainProof.IsValidated.ToString().ToLowerInvariant()}'.");

                if (impactedResults.Length == 2)
                {
                    ProductionControlPlaneLedgerCausalChainProofOutput.Write(
                        output,
                        controlPlaneId,
                        impactedResults[0].Context.Tenant.TenantId,
                        impactedResults[1].Context.Tenant.TenantId,
                        impactedResults[0].Inventory.RuntimeInstanceId,
                        impactedResults[1].Inventory.RuntimeInstanceId,
                        causalChainProof,
                        crossTenantLedgerLeakDetected,
                        infraLedgerValidated);
                }

                WriteTiming("Query and validate tenant-scoped ledger and control-plane causal chain");

                WritePhaseHeader(
                    8,
                    "FINAL PRODUCTION PROOF",
                    "[PASS TARGET] Re-query final recovery forensics, prove safe-tenant non-impact, and summarize all impacted and safe tenants dynamically.");

                var finalForensicsTasks =
                    impactedResults
                        .Select(async result => new
                        {
                            Result = result,
                            Records = await ProductionRealRuntimeCrashRecoveryTestHelpers
                                .AssertRecoveredInventoryForensicsAsync(
                                    output,
                                    forensicsQueryService,
                                    result.Recovery,
                                    TimeSpan.FromSeconds(60))
                                .ConfigureAwait(false)
                        })
                        .ToArray();

                var finalForensicsResults =
                    await Task
                        .WhenAll(finalForensicsTasks)
                        .ConfigureAwait(false);

                var finalForensicsRecords =
                    finalForensicsResults
                        .SelectMany(result => result.Records)
                        .ToArray();

                ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoDuplicateRecoveryForensics(
                    finalForensicsRecords);

                for (var leftIndex = 0; leftIndex < finalForensicsResults.Length; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < finalForensicsResults.Length; rightIndex++)
                    {
                        var left = finalForensicsResults[leftIndex];
                        var right = finalForensicsResults[rightIndex];

                        ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantRecoveryForensicsLeak(
                            left.Result.Recovery,
                            left.Records,
                            right.Result.Recovery,
                            right.Records);
                    }
                }

                Assert.DoesNotContain(
                    finalForensicsRecords,
                    record => !string.IsNullOrWhiteSpace(record.TenantId) && safeTenantIds.Contains(record.TenantId));

                output.WriteLine(string.Empty);
                output.WriteLine("# FINAL FORENSICS TIMELINE PROOF");

                foreach (var forensicsResult in finalForensicsResults)
                {
                    ProductionTenantRecoveryFinalProofHelper.WriteForensicsTimelineProof(
                        output.WriteLine,
                        forensicsResult.Result.Context.Tenant.TenantId,
                        forensicsResult.Records,
                        record => record.ForensicsId,
                        record => record.ExecutionId,
                        record => record.SharedRunId,
                        record => record.TenantId,
                        record => record.RuntimeFailureIncidentId,
                        record => record.Timeline.Select(item => item.EventType).ToArray());

                    output.WriteLine(string.Empty);
                }

                output.WriteLine("# SAFE TENANT NON-IMPACT PROOF");

                foreach (var safeResult in safeResults)
                {
                    var statusResult =
                        safeStatusResults.Single(result => string.Equals(
                            result.Result.Context.Tenant.TenantId,
                            safeResult.Context.Tenant.TenantId,
                            StringComparison.Ordinal));

                    var replayResult =
                        safeReplayResults.Single(result => string.Equals(
                            result.Result.Context.Tenant.TenantId,
                            safeResult.Context.Tenant.TenantId,
                            StringComparison.Ordinal));

                    output.WriteLine($"TenantId='{safeResult.Context.Tenant.TenantId}'");
                    output.WriteLine($"  Runtime='{safeResult.Inventory.RuntimeInstanceId}'");
                    output.WriteLine($"  SubmittedRuns='{safeResult.Inventory.Works.Count}'");
                    output.WriteLine($"  CompletedRuns='{statusResult.Statuses.Count}'");
                    output.WriteLine($"  ReplayProofs='{replayResult.Proofs.Count}'");
                    output.WriteLine("  RecoveredWork='0'");
                    output.WriteLine("  RecoveryForensics='0'");
                    output.WriteLine("  RuntimeProcessKilled='false'");
                    output.WriteLine("  CrashImpacted='false'");
                }

                var finalRecoveryProofs =
                    finalForensicsResults
                        .Select(result => new
                        {
                            Result = result.Result,
                            Proof = ProductionTenantRecoveryFinalProofHelper.Build(
                                result.Result.Context.Tenant.TenantId,
                                result.Result.Recovery.RecoveredWorks,
                                result.Records,
                                work => work.Original.Kind == RealRuntimeCrashWorkKind.InFlightExecution,
                                record => record.RuntimeFailureIncidentId,
                                record => record.ForensicsId,
                                record => record.Timeline.Select(item => item.EventType).ToArray())
                        })
                        .ToArray();

                WriteTiming("Query final runtime recovery forensics timelines and safe-tenant non-impact proof");
                WriteTiming("Scenario finalization");
                WriteTimingSummary();

                output.WriteLine(string.Empty);

                ProductionTenantLedgerSummaryOutput.Write(
                    output,
                    "TENANT-SCOPED LEDGER SUMMARY",
                    impactedLedgerResults
                        .Select(result =>
                            new ProductionTenantLedgerSummary(
                                result.Result.Context.Tenant.TenantId,
                                result.Query.RuntimeInstanceIds,
                                result.Query.ExecutionIds,
                                result.Query.Entries))
                        .ToArray(),
                    maxLedgerEntriesPerTenant: 50,
                    maxEventTypeRowsPerTenant: 30,
                    maxLedgerEntriesPerExecution: 25);

                output.WriteLine(string.Empty);
                output.WriteLine($"[{profile.LogPrefix} MULTI-TENANT CRASH SAFE-TENANT FINAL PROOF]");
                output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
                output.WriteLine($"TotalElapsed='{scenarioStopwatch.Elapsed}'");
                output.WriteLine($"ImpactedTenantCount='{impactedResults.Length}'");
                output.WriteLine($"SafeTenantCount='{safeResults.Length}'");

                foreach (var finalProof in finalRecoveryProofs)
                {
                    var replayResult =
                        impactedReplayResults.Single(result => string.Equals(
                            result.Result.Context.Tenant.TenantId,
                            finalProof.Result.Context.Tenant.TenantId,
                            StringComparison.Ordinal));

                    output.WriteLine(string.Empty);
                    output.WriteLine($"ImpactedTenant='{finalProof.Result.Context.Tenant.TenantId}'");
                    output.WriteLine($"  FailedRuntime='{finalProof.Result.Inventory.RuntimeInstanceId}'");
                    output.WriteLine($"  ForensicsTimelineTypes='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsTimelineTypes(finalProof.Proof)}'");
                    output.WriteLine($"  RecoveryModes='{ProductionTenantRecoveryFinalProofHelper.FormatRecoveryModes(finalProof.Proof)}'");
                    output.WriteLine($"  RuntimeFailureIncidentIds='{ProductionTenantRecoveryFinalProofHelper.FormatRuntimeFailureIncidentIds(finalProof.Proof)}'");
                    output.WriteLine($"  ForensicsIds='{ProductionTenantRecoveryFinalProofHelper.FormatForensicsIds(finalProof.Proof)}'");
                    output.WriteLine($"  Recovered='{finalProof.Result.Recovery.RecoveredWorks.Count}'");
                    output.WriteLine($"  Forensics='{finalForensicsResults.Single(result => string.Equals(result.Result.Context.Tenant.TenantId, finalProof.Result.Context.Tenant.TenantId, StringComparison.Ordinal)).Records.Count}'");
                    output.WriteLine($"  ReplayProof='{replayResult.Proofs.Count}'");
                }

                foreach (var safeResult in safeResults)
                {
                    var statusResult =
                        safeStatusResults.Single(result => string.Equals(
                            result.Result.Context.Tenant.TenantId,
                            safeResult.Context.Tenant.TenantId,
                            StringComparison.Ordinal));

                    var replayResult =
                        safeReplayResults.Single(result => string.Equals(
                            result.Result.Context.Tenant.TenantId,
                            safeResult.Context.Tenant.TenantId,
                            StringComparison.Ordinal));

                    output.WriteLine(string.Empty);
                    output.WriteLine($"SafeTenant='{safeResult.Context.Tenant.TenantId}'");
                    output.WriteLine($"  Runtime='{safeResult.Inventory.RuntimeInstanceId}'");
                    output.WriteLine($"  SubmittedRuns='{safeResult.Inventory.Works.Count}'");
                    output.WriteLine($"  CompletedRuns='{statusResult.Statuses.Count}'");
                    output.WriteLine($"  ReplayProof='{replayResult.Proofs.Count}'");
                    output.WriteLine("  Recovered='0'");
                    output.WriteLine("  Forensics='0'");
                    output.WriteLine("  RuntimeProcessKilled='false'");
                    output.WriteLine("  CrashImpacted='false'");
                }

                output.WriteLine(string.Empty);
                output.WriteLine("Safety:");
                output.WriteLine("  ForensicsValidated='true'");
                output.WriteLine("  StrictResumeValidated='true'");
                output.WriteLine("  SafeTenantNonImpactValidated='true'");
                output.WriteLine("  ReplayValidated='true'");
                output.WriteLine("  LedgerValidated='true'");
                output.WriteLine("  TraceValidated='true'");
                output.WriteLine($"  InfraLedgerValidated='{infraLedgerValidated.ToString().ToLowerInvariant()}'");
                output.WriteLine($"  ControlPlaneCausalChainValidated='{causalChainProof.IsValidated.ToString().ToLowerInvariant()}'");
                output.WriteLine("  CrossTenantLeakDetected='false'");
                output.WriteLine($"  CrossTenantLedgerLeakDetected='{crossTenantLedgerLeakDetected.ToString().ToLowerInvariant()}'");
                output.WriteLine("  SafeTenantRecoveryLeakDetected='false'");
                output.WriteLine("  DuplicateRecoveryDetected='false'");
                output.WriteLine(string.Empty);
            }
            finally
            {
                foreach (var httpClient in tenantHttpClients)
                {
                    httpClient.Dispose();
                }
            }
        }

        /// <summary>
        /// Executes multiple isolated multi-tenant process-host crash-recovery scenarios concurrently.
        /// </summary>
        /// <param name="parallelism">
        /// The number of crash-recovery scenarios to execute concurrently.
        /// </param>
        /// <returns>
        /// A task that completes when every concurrent scenario has completed.
        /// </returns>
        /// <exception cref="AggregateException">
        /// Thrown when one or more concurrent crash-recovery scenarios fail.
        /// </exception>
        protected async Task ExecuteMultiTenantCrashRecoveryScenariosInParallelAsync(
            int parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                parallelism,
                1);

            await using var dataStoreTrafficObserver =
                await ProductionDataStoreTrafficObserver
                    .StartAsync(output)
                    .ConfigureAwait(false);

            var harnessBudget =
                CreateParallelScenarioHarnessBudget(
                    parallelism);

            var overallStopwatch = Stopwatch.StartNew();

            output.WriteLine(
                $"# PARALLEL CRASH-RECOVERY PROOF - STARTING {parallelism} SCENARIOS");

            output.WriteLine(
                $"[PARALLEL HARNESS BUDGET] " +
                $"Parallelism='{harnessBudget.Parallelism}', " +
                $"PressureStepCount='{harnessBudget.PressureStepCount}', " +
                $"KillAfterCompletedStepCount='{harnessBudget.KillAfterCompletedStepCount}', " +
                $"ScaleOutTimeout='{harnessBudget.ScaleOutTimeout}', " +
                $"RuntimeReadinessTimeout='{harnessBudget.RuntimeReadinessTimeout}', " +
                $"DispatchTimeout='{harnessBudget.DispatchTimeout}', " +
                $"RecoveryRedispatchTimeout='{harnessBudget.RecoveryRedispatchTimeout}', " +
                $"ProgressTimeout='{harnessBudget.ProgressTimeout}', " +
                $"UnsafeTimeout='{harnessBudget.UnsafeTimeout}', " +
                $"RequeueTimeout='{harnessBudget.RequeueTimeout}', " +
                $"ExecutionResolveTimeout='{harnessBudget.ExecutionResolveTimeout}', " +
                $"CompletionTimeout='{harnessBudget.CompletionTimeout}'.");

            output.WriteLine(
                $"[PARALLEL SUMMARY] Parallelism='{parallelism}', " +
                $"ExpectedTenants='{parallelism * 3}', " +
                $"ExpectedSubmittedRuns='{parallelism * 9}', " +
                $"ExpectedImpactedTenants='{parallelism * 2}', " +
                $"ExpectedSafeTenants='{parallelism}'.");

            var scenarioTasks = Enumerable
                .Range(
                    1,
                    parallelism)
                .Select(
                    scenarioNumber =>
                    {
                        var scenarioId = Guid.NewGuid()
                            .ToString("N")[..8];

                        return ExecuteScenarioWithDiagnosticsAsync(
                            scenarioNumber,
                            parallelism,
                            scenarioId);
                    })
                .ToArray();

            var results = await Task
                .WhenAll(scenarioTasks)
                .ConfigureAwait(false);

            overallStopwatch.Stop();

            output.WriteLine(string.Empty);
            output.WriteLine(
                "# PARALLEL CRASH-RECOVERY PROOF - RESULTS");

            foreach (var result in results.OrderBy(
                         result => result.ScenarioNumber))
            {
                output.WriteLine(
                    $"[PARALLEL SCENARIO {result.ScenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{result.ScenarioId}', " +
                    $"Outcome='{(result.Exception is null ? "PASSED" : "FAILED")}', " +
                    $"Duration='{result.Duration}'.");

                if (result.Exception is null)
                {
                    continue;
                }

                output.WriteLine(
                    $"[PARALLEL SCENARIO {result.ScenarioNumber}/{parallelism} FAILURE] " +
                    $"ScenarioId='{result.ScenarioId}', " +
                    $"ExceptionType='{result.Exception.GetType().FullName}', " +
                    $"Message='{result.Exception.Message}'.");

                output.WriteLine(
                    result.Exception.ToString());
            }

            var failures = results
                .Where(result => result.Exception is not null)
                .ToArray();

            output.WriteLine(string.Empty);

            output.WriteLine(
                $"[PARALLEL SUMMARY] " +
                $"Parallelism='{parallelism}', " +
                $"Passed='{results.Length - failures.Length}', " +
                $"Failed='{failures.Length}', " +
                $"TotalDuration='{overallStopwatch.Elapsed}'.");

            if (failures.Length > 0)
            {
                throw new AggregateException(
                    $"{failures.Length} of {parallelism} parallel crash-recovery scenarios failed.",
                    failures.Select(result => result.Exception!));
            }
        }

        /// <summary>
        /// Executes one isolated multi-tenant process-host crash-recovery scenario
        /// and captures its diagnostic result without interrupting sibling scenarios.
        /// </summary>
        /// <param name="scenarioNumber">The one-based scenario number.</param>
        /// <param name="parallelism">The total number of concurrent scenarios.</param>
        /// <param name="scenarioId">The diagnostic scenario identifier.</param>
        /// <returns>The captured scenario result.</returns>
        private async Task<ParallelScenarioResult> ExecuteScenarioWithDiagnosticsAsync(
            int scenarioNumber,
            int parallelism,
            string scenarioId)
        {
            var stopwatch = Stopwatch.StartNew();

            output.WriteLine(string.Empty);

            output.WriteLine(
                $"# PARALLEL SCENARIO {scenarioNumber}/{parallelism} - START");

            output.WriteLine(
                $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                $"ScenarioId='{scenarioId}', " +
                $"StartedAtUtc='{DateTimeOffset.UtcNow:O}'.");

            try
            {
                await ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace(
                        parallelism)
                    .ConfigureAwait(false);

                stopwatch.Stop();

                output.WriteLine(
                    $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{scenarioId}', " +
                    $"Outcome='PASSED', " +
                    $"Duration='{stopwatch.Elapsed}'.");

                return new ParallelScenarioResult(
                    scenarioNumber,
                    scenarioId,
                    stopwatch.Elapsed,
                    Exception: null);
            }
            catch (Exception exception)
            {
                stopwatch.Stop();

                output.WriteLine(
                    $"[PARALLEL SCENARIO {scenarioNumber}/{parallelism}] " +
                    $"ScenarioId='{scenarioId}', " +
                    $"Outcome='FAILED', " +
                    $"Duration='{stopwatch.Elapsed}', " +
                    $"ExceptionType='{exception.GetType().FullName}', " +
                    $"Message='{exception.Message}'.");

                return new ParallelScenarioResult(
                    scenarioNumber,
                    scenarioId,
                    stopwatch.Elapsed,
                    exception);
            }
        }

        /// <summary>
        /// Represents the captured outcome of one parallel crash-recovery scenario.
        /// </summary>
        /// <param name="ScenarioNumber">The one-based scenario number.</param>
        /// <param name="ScenarioId">The diagnostic scenario identifier.</param>
        /// <param name="Duration">The total scenario duration.</param>
        /// <param name="Exception">The captured failure, when present.</param>
        private sealed record ParallelScenarioResult(
            int ScenarioNumber,
            string ScenarioId,
            TimeSpan Duration,
            Exception? Exception);

        /// <summary>
        /// Builds a deterministic harness budget that grows with parallel pressure
        /// without modifying any production Redis or provider timeout.
        /// </summary>
        /// <param name="parallelism">The number of concurrently executed scenarios.</param>
        /// <returns>The resolved harness-only timeout and crash-boundary budget.</returns>
        private static ParallelScenarioHarnessBudget CreateParallelScenarioHarnessBudget(
            int parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                parallelism,
                1);

            var pressureStepCount =
                parallelism <= 15
                    ? 0
                    : ((parallelism - 16) / 5) + 1;

            return new ParallelScenarioHarnessBudget(
                Parallelism: parallelism,
                PressureStepCount: pressureStepCount,
                KillAfterCompletedStepCount:
                    Math.Max(
                        10,
                        KillAfterCompletedStepCount - (pressureStepCount * 10)),
                ScaleOutTimeout:
                    TimeSpan.FromMinutes(
                        2 + (pressureStepCount * 3)),
                RuntimeReadinessTimeout:
                    TimeSpan.FromSeconds(
                        30 + (pressureStepCount * 150)),
                DispatchTimeout:
                    TimeSpan.FromMinutes(
                        3 + (pressureStepCount * 5)),
                RecoveryRedispatchTimeout:
                    TimeSpan.FromMinutes(
                        3 + (pressureStepCount * 5)) +
                    TimeSpan.FromSeconds(
                        30 + (pressureStepCount * 150)) +
                    TimeSpan.FromMinutes(1),
                ProgressTimeout:
                    TimeSpan.FromMinutes(
                        3 + pressureStepCount),
                UnsafeTimeout:
                    TimeSpan.FromSeconds(
                        60 + (pressureStepCount * 30)),
                RequeueTimeout:
                    TimeSpan.FromMinutes(
                        3 + (pressureStepCount * 2)),
                ExecutionResolveTimeout:
                    TimeSpan.FromSeconds(
                        60 + (pressureStepCount * 30)),
                CompletionTimeout:
                    TimeSpan.FromMinutes(
                        3 + (pressureStepCount * 2)));
        }

        /// <summary>
        /// Represents timeout and crash-boundary values used only by the parallel integration harness.
        /// </summary>
        private sealed record ParallelScenarioHarnessBudget(
            int Parallelism,
            int PressureStepCount,
            int KillAfterCompletedStepCount,
            TimeSpan ScaleOutTimeout,
            TimeSpan RuntimeReadinessTimeout,
            TimeSpan DispatchTimeout,
            TimeSpan RecoveryRedispatchTimeout,
            TimeSpan ProgressTimeout,
            TimeSpan UnsafeTimeout,
            TimeSpan RequeueTimeout,
            TimeSpan ExecutionResolveTimeout,
            TimeSpan CompletionTimeout);

        /// <summary>
        /// Resolves the runtime signal subscriber only when hybrid observation is enabled.
        /// </summary>
        /// <param name="services">The scenario service provider.</param>
        /// <returns>The runtime signal subscriber in hybrid mode; otherwise, <c>null</c>.</returns>
        private IAiRuntimeSignalSubscriber? ResolveSignalSubscriber(
            IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            return ObservationMode == ProductionRecoveryObservationMode.HybridSignals
                ? services.GetRequiredService<IAiRuntimeSignalSubscriber>()
                : null;
        }

        /// <summary>
        /// Waits for durable DAG progress using the configured observation mode.
        /// </summary>
        private async Task WaitForDagCompletedStepCountByObservationModeAsync(
            IAiDagExecutionStore dagStore,
            IAiRuntimeSignalSubscriber? signalSubscriber,
            string controlPlaneId,
            string executionId,
            int minimumCompletedSteps,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            if (ObservationMode == ProductionRecoveryObservationMode.HybridSignals)
            {
                ArgumentNullException.ThrowIfNull(signalSubscriber);

                await ProductionRecoveryWaitHelpers
                    .WaitForDagCompletedStepCountHybridAsync(
                        dagStore,
                        signalSubscriber,
                        controlPlaneId,
                        executionId,
                        minimumCompletedSteps,
                        timeout,
                        HybridFallbackPollInterval)
                    .ConfigureAwait(false);

                return;
            }

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    executionId,
                    minimumCompletedSteps,
                    timeout)
                .ConfigureAwait(false);
        }

        private async Task WaitForScaleOutWatcherReadyAsync(
            IServiceProvider services,
            string controlPlaneId)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            var watcher = services
                .GetServices<IHostedService>()
                .OfType<AiRuntimeScaleOutRequestWatcherHostedService>()
                .SingleOrDefault()
                ?? throw new InvalidOperationException(
                    "The runtime scale-out request watcher hosted service is not registered.");

            await watcher
                .WaitUntilReadyAsync(ScaleOutWatcherReadinessTimeout)
                .ConfigureAwait(false);

            Assert.True(
                string.Equals(
                    watcher.ResolvedControlPlaneId,
                    controlPlaneId,
                    StringComparison.Ordinal),
                $"Scale-out watcher resolved an unexpected control-plane id. Expected='{controlPlaneId}', Actual='{watcher.ResolvedControlPlaneId}'.");

            output.WriteLine(
                $"[SCALE-OUT WATCHER READY] WatcherId='{watcher.WatcherId}', ControlPlaneId='{watcher.ResolvedControlPlaneId ?? controlPlaneId}', ReadyAtUtc='{watcher.ReadyAtUtc:O}', Timeout='{ScaleOutWatcherReadinessTimeout}'.");
        }

        private sealed record ProcessHostTenantScenarioContext(
            ProductionTenantScenarioDefinition Tenant,
            McpTestClient Mcp,
            string PipelinePrefix,
            bool IsSafe);

        private sealed record ProcessHostImpactedTenantExecutionResult(
            ProcessHostTenantScenarioContext Context,
            RealRuntimeCrashAssignedWorkInventoryProof Inventory,
            RealRuntimeCrashFailedRuntimeRecoveryProof Recovery);

        private sealed record ProcessHostSafeTenantExecutionResult(
            ProcessHostTenantScenarioContext Context,
            RealRuntimeCrashAssignedWorkInventoryProof Inventory);

        private static bool IsSafeCrashScenarioTenant(
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);

            return tenant.TenantId.Contains("safe", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(tenant.RuntimeInstanceIdPrefix) &&
                    tenant.RuntimeInstanceIdPrefix.Contains("safe", StringComparison.OrdinalIgnoreCase));
        }

        private static ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario(
            bool includeSafeTenant = false)
        {
            var baseScenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var templateTenant =
                baseScenario.Tenants.Single();

            var tenantA =
                templateTenant with
                {
                    TenantId = "tenant-real-crash-a",
                    TenantGroupId = "tenant-real-crash-a-group",
                    RuntimeInstanceIdPrefix = "tenant-real-crash-a-runtime",
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 2,
                    Run = templateTenant.Run with
                    {
                        RunCount = 3,
                        StepCount = MultiTenantStepCount,
                        DelayMs = 750,
                        FlakyStepInterval = FlakyStepIntervalMs,
                        EnableRetention = true
                    }
                };

            var tenantB =
                templateTenant with
                {
                    TenantId = "tenant-real-crash-b",
                    TenantGroupId = "tenant-real-crash-b-group",
                    RuntimeInstanceIdPrefix = "tenant-real-crash-b-runtime",
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 2,
                    Run = templateTenant.Run with
                    {
                        RunCount = 3,
                        StepCount = MultiTenantStepCount,
                        DelayMs = 750,
                        FlakyStepInterval = FlakyStepIntervalMs,
                        EnableRetention = true
                    }
                };

            var tenants =
                new List<ProductionTenantScenarioDefinition>
                {
                    tenantA,
                    tenantB
                };

            if (includeSafeTenant)
            {
                var safeTenant =
                    templateTenant with
                    {
                        TenantId = "tenant-real-crash-safe",
                        TenantGroupId = "tenant-real-crash-safe-group",
                        RuntimeInstanceIdPrefix = "tenant-real-crash-safe-runtime",
                        MaxRuntimeInstances = 2,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 2,
                        Run = templateTenant.Run with
                        {
                            RunCount = 3,
                            StepCount = MultiTenantStepCount,
                            DelayMs = 750,
                            FlakyStepInterval = FlakyStepIntervalMs,
                            EnableRetention = true
                        }
                    };

                tenants.Add(safeTenant);
            }

            return baseScenario with
            {
                Name = includeSafeTenant
                    ? "http-process-host-real-runtime-crash-recovery-two-tenant-plus-safe-inventory"
                    : "http-process-host-real-runtime-crash-recovery-two-tenant-inventory",
                ControlPlaneIdPrefix = includeSafeTenant
                    ? "http-process-host-real-runtime-crash-recovery-two-tenant-plus-safe-inventory"
                    : "http-process-host-real-runtime-crash-recovery-two-tenant-inventory",
                Tenants = tenants.ToArray(),
                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,
                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(3),
                CompletionTimeout = TimeSpan.FromMinutes(7),
                Assertions = new ProductionRuntimeScenarioAssertionOptions
                {
                    AssertAllRunsCompleted = true,
                    AssertTenantIsolation = true,
                    AssertScaleOut = true,
                    AssertMaxRuntimeInstances = true,
                    AssertLedger = true,
                    AssertTrace = true,
                    AssertReplayReport = true,
                    AssertReplayLedger = true,
                    AssertReplayTrace = true
                }
            };
        }

        private static bool IsInfraLedgerEntry(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            return entry.EventType.StartsWith(
                    "control.",
                    StringComparison.Ordinal) ||
                entry.EventType.Contains(
                    "runtime-instance",
                    StringComparison.Ordinal) ||
                entry.EventType.Contains(
                    "runtime-execution-recovery",
                    StringComparison.Ordinal) ||
                entry.EventType.Contains(
                    "recovery",
                    StringComparison.Ordinal) ||
                string.Equals(
                    entry.CorrelationContext.Operation,
                    "control-plane",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool LedgerEntryContainsTenant(
            AiDecisionLedgerEntry entry,
            string tenantId)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

            if (entry.Metadata is null)
            {
                return false;
            }

            return entry.Metadata.TryGetValue("tenantId", out var value) &&
                string.Equals(value, tenantId, StringComparison.Ordinal) ||
                entry.Metadata.TryGetValue("tenant.id", out value) &&
                string.Equals(value, tenantId, StringComparison.Ordinal);
        }

        private static void AssertAllRuntimeStatusesCompleted(
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> finalStatuses)
        {
            foreach (var finalStatus in finalStatuses)
            {
                Assert.True(
                    finalStatus.Success,
                    finalStatus.FailureReason ?? finalStatus.Message);

                Assert.True(
                    string.Equals(finalStatus.RunState?.Status, "completed", StringComparison.OrdinalIgnoreCase),
                    $"Recovered work did not complete. RuntimeInstanceId='{finalStatus.RuntimeInstanceId}', RunId='{finalStatus.RunId}', ExecutionId='{finalStatus.ExecutionId}', Status='{finalStatus.RunState?.Status}', FailureReason='{finalStatus.FailureReason}', Message='{finalStatus.Message}'.");
            }
        }
    }
}
