using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Models;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Profiles;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios;
using Multiplexed.AI.Stores;
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
        private const int KillAfterCompletedStepCount = 50;
        private const int MultiTenantStepCount = 100;
        private const int FlakyStepIntervalMs = 500;



        private readonly ITestOutputHelper output;
        private readonly IProcessHostScenarioRuntimeProfile profile;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="profile">The process-host scenario runtime profile.</param>
        protected ProcessHostRealRuntimeCrashRecoveryScenarioTestsBase(
            ITestOutputHelper output,
            IProcessHostScenarioRuntimeProfile profile)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

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
                output.WriteLine($"  HostCreationMode='{scenario.HostCreationMode}'");
                output.WriteLine($"  PersistenceProfile='{scenario.PersistenceProfile}'");
                output.WriteLine($"  ObservabilityProfile='{scenario.ObservabilityProfile}'");
                output.WriteLine($"  RuntimeHostAssemblyPath='{currentRuntimeHostAssemblyPath}'");
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

            var processControl =
                host.Services.GetRequiredService<IAiRuntimeHostProcessControl>();

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

            WriteTiming("Setup host services and tenant MCP clients");

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
                        progressTimeout: TimeSpan.FromMinutes(3));

            var tenantBInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        tenantBMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
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
                        progressTimeout: TimeSpan.FromMinutes(3));

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
                        dagStore,
                        tenantAInventory,
                        unsafeTimeout: TimeSpan.FromSeconds(60),
                        requeueTimeout: TimeSpan.FromSeconds(180),
                        redispatchTimeout: scenario.DispatchTimeout,
                        executionResolveTimeout: TimeSpan.FromSeconds(60));

            var tenantBRecoveryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                        output,
                        processControl,
                        registry,
                        runExecutionIndex,
                        sharedRunStore,
                        dagStore,
                        tenantBInventory,
                        unsafeTimeout: TimeSpan.FromSeconds(60),
                        requeueTimeout: TimeSpan.FromSeconds(180),
                        redispatchTimeout: scenario.DispatchTimeout,
                        executionResolveTimeout: TimeSpan.FromSeconds(60));

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
                    tenantARecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    output,
                    dagStore,
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
                    failedRuntimeUnsafeValidated);

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

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var processControl =
                host.Services.GetRequiredService<IAiRuntimeHostProcessControl>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

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

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
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

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    executionId,
                    StepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            output.WriteLine(
                $"[{profile.LogPrefix} REAL RUNTIME CRASH PROOF] Recovered redispatch DAG execution completed all durable steps. RecoveredExecutionId='{recoveredExecutionId}', CompletedSteps='{StepCount}'.");
        }

        /// <summary>
        /// Verifies that two impacted tenants recover real process-host runtime crashes while a third safe tenant
        /// continues normal execution without recovery, forensics, redispatch, or cross-tenant leakage.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        protected async Task ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            var scenario =
                CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario(
                    includeSafeTenant: true);

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
                    $"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT TIMING] Phase='{phaseName}', Duration='{elapsed}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");

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
                output.WriteLine($"# {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST TWO-TENANT CRASH SAFE-TENANT TIMING SUMMARY");

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

            var processControl =
                host.Services.GetRequiredService<IAiRuntimeHostProcessControl>();

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

            var safeTenant =
                scenario.Tenants.Single(tenant =>
                    string.Equals(
                        tenant.TenantId,
                        "tenant-real-crash-safe",
                        StringComparison.Ordinal));

            using var tenantAHttpClient =
                host.CreateClient();

            using var tenantBHttpClient =
                host.CreateClient();

            using var safeTenantHttpClient =
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

            var safeTenantMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        safeTenantHttpClient,
                        profile.RequestedBy,
                        tenantId: safeTenant.TenantId,
                        tenantGroupId: safeTenant.TenantGroupId)
                    .ConfigureAwait(false);

            WriteTiming("Setup host services and tenant MCP clients");

            var ledgerTimelineFromUtc =
                DateTimeOffset.UtcNow.AddSeconds(-5);

            var tenantAPipelinePrefix =
                $"{scenario.Name}-{tenantA.TenantId}-real-crash-{Guid.NewGuid():N}";

            var tenantBPipelinePrefix =
                $"{scenario.Name}-{tenantB.TenantId}-real-crash-{Guid.NewGuid():N}";

            var safeTenantPipelinePrefix =
                $"{scenario.Name}-{safeTenant.TenantId}-safe-{Guid.NewGuid():N}";

            output.WriteLine($"# SCENARIO INTRO - {profile.ProviderName.ToUpperInvariant()} PROCESS-HOST TWO-TENANT CRASH RECOVERY WITH SAFE TENANT");
            output.WriteLine("Executive proof: this scenario kills one real external runtime process for tenant A and tenant B, recovers their assigned work, and proves that tenant C continues normal execution without recovery, forensics, redispatch, or cross-tenant leakage.");
            output.WriteLine(string.Empty);
            output.WriteLine("Scenario contract:");
            output.WriteLine("  - [ON] Real external runtime host processes are used; no fixture runtime is accepted for this scenario.");
            output.WriteLine("  - [ON] Tenant A and tenant B must each lose one unsafe runtime instance.");
            output.WriteLine("  - [ON] Tenant C is safe and must not be killed, redispatched for recovery, or receive recovery forensics.");
            output.WriteLine("  - [ON] Impacted in-flight DAG executions must resume with the same durable execution id.");
            output.WriteLine("  - [ON] Impacted local queued work must be recovered through durable shared-run redispatch.");
            output.WriteLine("  - [ON] Safe tenant runs must complete normally and expose replay, ledger, and trace evidence.");
            output.WriteLine("  - [ON] No cross-tenant leak, duplicate recovery, or safe-tenant recovery contamination is allowed.");
            output.WriteLine(string.Empty);
            output.WriteLine("Workload summary:");
            output.WriteLine($"  StepCount='{MultiTenantStepCount}'");
            output.WriteLine($"  KillAfterCompletedStepCount='{KillAfterCompletedStepCount}'");
            output.WriteLine($"  FlakyStepIntervalMs='{FlakyStepIntervalMs}'");
            output.WriteLine("  TenantCount='3'");
            output.WriteLine("  ImpactedTenantCount='2'");
            output.WriteLine("  SafeTenantCount='1'");
            output.WriteLine("  RunsPerTenant='3'");
            output.WriteLine("  SubmittedRuns='9'");
            output.WriteLine("  ExpectedRecoveredWork='6'");
            output.WriteLine("  ExpectedSafeTenantRecoveredWork='0'");
            output.WriteLine("  ExpectedReplayValidatedExecutions='9'");
            output.WriteLine("  TotalValidatedExecutionFlows='15'");
            output.WriteLine(string.Empty);
            output.WriteLine("Runtime profile:");
            output.WriteLine($"  Provider='{profile.ProviderName}'");
            output.WriteLine($"  ProviderLabel='{profile.ProviderLabel}'");
            output.WriteLine($"  ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"  HostCreationMode='{scenario.HostCreationMode}'");
            output.WriteLine($"  PersistenceProfile='{scenario.PersistenceProfile}'");
            output.WriteLine($"  ObservabilityProfile='{scenario.ObservabilityProfile}'");
            output.WriteLine($"  RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'");
            output.WriteLine(string.Empty);
            output.WriteLine("Timeout budget:");
            output.WriteLine($"  ScaleOutTimeout: {scenario.ScaleOutTimeout}");
            output.WriteLine($"  DispatchTimeout: {scenario.DispatchTimeout}");
            output.WriteLine($"  CompletionTimeout: {scenario.CompletionTimeout}");
            output.WriteLine(string.Empty);
            output.WriteLine($"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT PROOF] Starting. ControlPlaneId='{controlPlaneId}', TenantAPipelinePrefix='{tenantAPipelinePrefix}', TenantBPipelinePrefix='{tenantBPipelinePrefix}', SafeTenantPipelinePrefix='{safeTenantPipelinePrefix}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            WriteTiming("Scenario identifiers and intro output");

            WritePhaseHeader(
                1,
                "BUILD ASSIGNED WORK INVENTORY FOR IMPACTED AND SAFE TENANTS",
                "[PASS TARGET] Submit three runs per tenant, build the same assigned-work inventory for tenant A, tenant B, and the safe tenant, then kill only tenant A and tenant B runtime candidates.");

            var tenantAInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        tenantAMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
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
                        progressTimeout: TimeSpan.FromMinutes(3));

            var tenantBInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        tenantBMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
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
                        progressTimeout: TimeSpan.FromMinutes(3));

            var safeTenantInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        output,
                        safeTenantMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        safeTenant,
                        controlPlaneId,
                        safeTenantPipelinePrefix,
                        profile.RequestedBy,
                        profile.Source,
                        runCount: 3,
                        minimumInFlightExecutionCount: 1,
                        minimumLocalQueuedRunCount: 1,
                        minimumCompletedStepsBeforeKill: KillAfterCompletedStepCount,
                        scaleOutTimeout: scenario.ScaleOutTimeout,
                        dispatchTimeout: scenario.DispatchTimeout,
                        progressTimeout: TimeSpan.FromMinutes(3));

            await Task
                .WhenAll(
                    tenantAInventoryTask,
                    tenantBInventoryTask,
                    safeTenantInventoryTask)
                .ConfigureAwait(false);

            var tenantAInventory =
                await tenantAInventoryTask.ConfigureAwait(false);

            var tenantBInventory =
                await tenantBInventoryTask.ConfigureAwait(false);

            var safeTenantInventory =
                await safeTenantInventoryTask.ConfigureAwait(false);

            WriteTiming("Build assigned work inventory for impacted and safe tenants");

            Assert.NotEqual(
                tenantAInventory.RuntimeInstanceId,
                tenantBInventory.RuntimeInstanceId);

            Assert.NotEqual(
                tenantAInventory.RuntimeInstanceId,
                safeTenantInventory.RuntimeInstanceId);

            Assert.NotEqual(
                tenantBInventory.RuntimeInstanceId,
                safeTenantInventory.RuntimeInstanceId);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                tenantAInventory.RuntimeInstanceId,
                tenantA);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                tenantBInventory.RuntimeInstanceId,
                tenantB);

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertRuntimeBelongsToTenant(
                safeTenantInventory.RuntimeInstanceId,
                safeTenant);

            WriteTiming("Validate selected failed runtime tenant ownership and safe tenant ownership");

            WritePhaseHeader(
                2,
                "KILL REAL RUNTIME PROCESSES AND WAIT AUTOMATIC RECOVERY",
                "[PASS TARGET] Kill one real process for tenant A and tenant B only. Safe tenant runtime is not killed and must not enter recovery.");

            var tenantARecoveryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                        output,
                        processControl,
                        registry,
                        runExecutionIndex,
                        sharedRunStore,
                        dagStore,
                        tenantAInventory,
                        unsafeTimeout: TimeSpan.FromSeconds(60),
                        requeueTimeout: TimeSpan.FromSeconds(180),
                        redispatchTimeout: scenario.DispatchTimeout,
                        executionResolveTimeout: TimeSpan.FromSeconds(60));

            var tenantBRecoveryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                        output,
                        processControl,
                        registry,
                        runExecutionIndex,
                        sharedRunStore,
                        dagStore,
                        tenantBInventory,
                        unsafeTimeout: TimeSpan.FromSeconds(60),
                        requeueTimeout: TimeSpan.FromSeconds(180),
                        redispatchTimeout: scenario.DispatchTimeout,
                        executionResolveTimeout: TimeSpan.FromSeconds(60));

            await Task
                .WhenAll(
                    tenantARecoveryTask,
                    tenantBRecoveryTask)
                .ConfigureAwait(false);

            var tenantARecovery =
                await tenantARecoveryTask.ConfigureAwait(false);

            var tenantBRecovery =
                await tenantBRecoveryTask.ConfigureAwait(false);

            Assert.DoesNotContain(
                tenantARecovery.RecoveredWorks.Concat(tenantBRecovery.RecoveredWorks),
                work => string.Equals(work.Original.SharedRun.AssignedRuntimeInstanceId, safeTenantInventory.RuntimeInstanceId, StringComparison.Ordinal));

            WriteTiming("Kill impacted runtime processes and wait for automatic recovery");

            WritePhaseHeader(
                3,
                "MCP RUNTIME RECOVERY FORENSICS PROOF",
                "[PASS TARGET] Every impacted recovered work item must have runtime recovery forensics with no cross-tenant leak, no duplicate recovery record, and no safe tenant forensics contamination.");

            var recoveries =
                new[]
                {
            tenantARecovery,
            tenantBRecovery
                };

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT STEP 1 - MCP FORENSICS PROOF] Starting recovery forensics validation.");

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

            Assert.DoesNotContain(
                tenantAForensics.Concat(tenantBForensics),
                record => string.Equals(record.TenantId, safeTenant.TenantId, StringComparison.Ordinal));

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT STEP 1 - MCP FORENSICS PROOF] " +
                $"TenantA='{tenantA.TenantId}', FailedRuntimeA='{tenantAInventory.RuntimeInstanceId}', ExpectedA='{tenantARecovery.RecoveredWorks.Count}', ActualA='{tenantAForensics.Count}', " +
                $"TenantB='{tenantB.TenantId}', FailedRuntimeB='{tenantBInventory.RuntimeInstanceId}', ExpectedB='{tenantBRecovery.RecoveredWorks.Count}', ActualB='{tenantBForensics.Count}', " +
                $"SafeTenant='{safeTenant.TenantId}', SafeRuntime='{safeTenantInventory.RuntimeInstanceId}', ExpectedSafeRecovery='0', ActualSafeRecovery='0', SafeTenantRecoveryForensicsDetected='false', CrossTenantLeakDetected='false', DuplicateRecoveryDetected='false'.");

            WriteTiming("Validate MCP runtime recovery forensics");

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantInventoryRecoveryLeak(
                recoveries);

            WriteTiming("Validate no cross-tenant inventory recovery leak");

            WritePhaseHeader(
                4,
                "RECOVERED AND SAFE TENANT DAG COMPLETION",
                "[PASS TARGET] Impacted recovered DAG executions and safe tenant normal DAG executions must all complete the configured step count.");

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    output,
                    dagStore,
                    tenantARecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    output,
                    dagStore,
                    tenantBRecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            var safeExecutionTasks =
                safeTenantInventory.Works
                    .Select(work =>
                        ProductionRecoveryWaitHelpers.WaitForDurableDagExecutionAsync(
                            sharedRunStore,
                            runExecutionIndex,
                            dagStore,
                            work.SharedRunId,
                            scenario.CompletionTimeout))
                    .ToArray();

            var safeExecutions =
                await Task
                    .WhenAll(safeExecutionTasks)
                    .ConfigureAwait(false);

            foreach (var safeExecution in safeExecutions)
            {
                await ProductionRecoveryWaitHelpers
                    .WaitForDagCompletedStepCountAsync(
                        dagStore,
                        safeExecution.ExecutionId,
                        MultiTenantStepCount,
                        scenario.CompletionTimeout)
                    .ConfigureAwait(false);

                output.WriteLine(
                    $"[{profile.LogPrefix} SAFE TENANT COMPLETION] Safe tenant DAG execution completed without crash recovery. TenantId='{safeTenant.TenantId}', SharedRunId='{safeExecution.SharedRun.SharedRunId}', RuntimeInstanceId='{safeExecution.SharedRun.AssignedRuntimeInstanceId}', LocalRunId='{safeExecution.SharedRun.LocalRunId}', ExecutionId='{safeExecution.ExecutionId}', CompletedSteps='{MultiTenantStepCount}'.");
            }

            WriteTiming("Wait for recovered and safe tenant DAG completion");

            WritePhaseHeader(
                5,
                "TERMINAL RUNTIME RUN STATUS CONVERGENCE",
                "[PASS TARGET] MCP runtime queue status must converge to completed for impacted recovered local runs and safe tenant normal local runs.");

            var tenantARedispatchedRuns =
                tenantARecovery.RecoveredWorks
                    .Select(work => work.RedispatchedRun)
                    .ToArray();

            var tenantBRedispatchedRuns =
                tenantBRecovery.RecoveredWorks
                    .Select(work => work.RedispatchedRun)
                    .ToArray();

            var safeTenantCompletedRuns =
                safeExecutions
                    .Select(execution => execution.SharedRun)
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

            var safeTenantFinalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        safeTenantMcp,
                        safeTenantCompletedRuns,
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            AssertAllRuntimeStatusesCompleted(
                tenantAFinalStatuses);

            AssertAllRuntimeStatusesCompleted(
                tenantBFinalStatuses);

            AssertAllRuntimeStatusesCompleted(
                safeTenantFinalStatuses);

            Assert.DoesNotContain(
                safeTenantFinalStatuses,
                status => string.Equals(status.RuntimeInstanceId, tenantAInventory.RuntimeInstanceId, StringComparison.Ordinal) ||
                    string.Equals(status.RuntimeInstanceId, tenantBInventory.RuntimeInstanceId, StringComparison.Ordinal));

            WriteTiming("Wait for terminal runtime run statuses");

            WritePhaseHeader(
                6,
                "MCP REPLAY LEDGER TRACE PROOF",
                "[PASS TARGET] MCP replay tooling must expose replay report, replay ledger, replay trace, execution ledger, execution trace, completion evidence, and step-completion evidence for impacted and safe tenant executions.");

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

            var safeTenantReplayProofs =
                await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                    .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                        safeTenantMcp,
                        safeTenant.TenantId,
                        safeTenantFinalStatuses,
                        profile.RequestedBy,
                        profile.Source)
                    .ConfigureAwait(false);

            ProductionRuntimeReplayOutput.WriteRecoveredExecutionReplayProof(
                output,
                tenantAReplayProofs,
                tenantBReplayProofs,
                safeTenantReplayProofs);

            WriteTiming("Validate MCP replay ledger and trace proof");

            WritePhaseHeader(
                7,
                "MCP TENANT-SCOPED LEDGER PROOF",
                "[PASS TARGET] Tenant-scoped MCP ledger queries must expose control-plane, runtime-instance, and recovery evidence for impacted tenants, while safe tenant remains absent from recovery evidence.");

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

            var safeTenantRecoveryEntriesVisibleFromImpactedQueries =
                ledgerEntries.Count(entry =>
                    LedgerEntryContainsTenant(
                        entry,
                        safeTenant.TenantId) &&
                    IsInfraLedgerEntry(entry) &&
                    entry.EventType.Contains("recovery", StringComparison.Ordinal));

            var crossTenantLedgerLeakDetected =
                tenantBEntriesVisibleFromTenantA > 0 ||
                tenantAEntriesVisibleFromTenantB > 0 ||
                safeTenantRecoveryEntriesVisibleFromImpactedQueries > 0;

            var tenantAInfraEntries =
                tenantALedgerEntries.Count(IsInfraLedgerEntry);

            var tenantBInfraEntries =
                tenantBLedgerEntries.Count(IsInfraLedgerEntry);

            var infraLedgerValidated =
                tenantAInfraEntries > 0 &&
                tenantBInfraEntries > 0;

            Assert.False(
                crossTenantLedgerLeakDetected,
                $"Cross-tenant ledger leak detected. TenantBEntriesVisibleFromTenantA='{tenantBEntriesVisibleFromTenantA}', TenantAEntriesVisibleFromTenantB='{tenantAEntriesVisibleFromTenantB}', SafeTenantRecoveryEntriesVisibleFromImpactedQueries='{safeTenantRecoveryEntriesVisibleFromImpactedQueries}'.");

            Assert.True(
                tenantAInfraEntries > 0,
                $"Tenant A scoped ledger query did not return infra/control-plane/runtime recovery evidence. TenantId='{tenantA.TenantId}', RuntimeIds='{string.Join(",", tenantALedgerQuery.RuntimeInstanceIds)}', ExecutionIds='{string.Join(",", tenantALedgerQuery.ExecutionIds)}'.");

            Assert.True(
                tenantBInfraEntries > 0,
                $"Tenant B scoped ledger query did not return infra/control-plane/runtime recovery evidence. TenantId='{tenantB.TenantId}', RuntimeIds='{string.Join(",", tenantBLedgerQuery.RuntimeInstanceIds)}', ExecutionIds='{string.Join(",", tenantBLedgerQuery.ExecutionIds)}'.");

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT STEP 3 - MCP LEDGER PROOF] " +
                $"TenantAEntries='{tenantALedgerEntries.Count}', TenantARuntimeIds='{tenantALedgerQuery.RuntimeInstanceIds.Count}', TenantAExecutionIds='{tenantALedgerQuery.ExecutionIds.Count}', TenantAInfraEntries='{tenantAInfraEntries}', TenantBEntriesVisibleFromTenantA='{tenantBEntriesVisibleFromTenantA}', " +
                $"TenantBEntries='{tenantBLedgerEntries.Count}', TenantBRuntimeIds='{tenantBLedgerQuery.RuntimeInstanceIds.Count}', TenantBExecutionIds='{tenantBLedgerQuery.ExecutionIds.Count}', TenantBInfraEntries='{tenantBInfraEntries}', TenantAEntriesVisibleFromTenantB='{tenantAEntriesVisibleFromTenantB}', " +
                $"SafeTenant='{safeTenant.TenantId}', SafeTenantRuntime='{safeTenantInventory.RuntimeInstanceId}', SafeTenantRecoveryEntriesVisibleFromImpactedQueries='{safeTenantRecoveryEntriesVisibleFromImpactedQueries}', " +
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
                    failedRuntimeUnsafeValidated);

            output.WriteLine(
                $"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT MCP CONTROL-PLANE LEDGER QUERY PROOF] " +
                $"ScenarioCausalChainEntries='{causalChainLedgerEntries.Count}', ControlPlaneEntries='{causalChainLedgerEntries.Count(entry => entry.EventType.StartsWith("control.", StringComparison.Ordinal))}', QueryScope='runtime-instance ids + execution ids + control-plane run execution ids + impacted scenario membership filter'.");

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
                "[PASS TARGET] Re-query final runtime recovery forensics after completion, print the causal forensics timeline first, prove safe tenant non-impact, then summarize recovery, replay, ledger, trace, timing, and safety invariants.");

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

            Assert.DoesNotContain(
                tenantAFinalForensics.Concat(tenantBFinalForensics),
                record => string.Equals(record.TenantId, safeTenant.TenantId, StringComparison.Ordinal));

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
            output.WriteLine("# SAFE TENANT NON-IMPACT PROOF");
            output.WriteLine($"TenantId='{safeTenant.TenantId}'");
            output.WriteLine($"SafeRuntime='{safeTenantInventory.RuntimeInstanceId}'");
            output.WriteLine($"SubmittedRuns='{safeTenantInventory.Works.Count}'");
            output.WriteLine($"CompletedRuns='{safeTenantFinalStatuses.Count}'");
            output.WriteLine($"ReplayProofs='{safeTenantReplayProofs.Count}'");
            output.WriteLine("RecoveredWork='0'");
            output.WriteLine("RecoveryForensics='0'");
            output.WriteLine("RuntimeProcessKilled='false'");
            output.WriteLine("CrashImpacted='false'");
            output.WriteLine(string.Empty);

            WriteTiming("Query final runtime recovery forensics timelines and safe tenant non-impact proof");

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

            output.WriteLine($"[{profile.LogPrefix} TWO-TENANT CRASH SAFE-TENANT FINAL PROOF]");
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
            output.WriteLine("SafeTenant:");
            output.WriteLine($"  TenantId='{safeTenant.TenantId}'");
            output.WriteLine($"  Runtime='{safeTenantInventory.RuntimeInstanceId}'");
            output.WriteLine($"  SubmittedRuns='{safeTenantInventory.Works.Count}'");
            output.WriteLine($"  CompletedRuns='{safeTenantFinalStatuses.Count}'");
            output.WriteLine($"  ReplayProof='{safeTenantReplayProofs.Count}'");
            output.WriteLine("  Recovered='0'");
            output.WriteLine("  Forensics='0'");
            output.WriteLine("  RuntimeProcessKilled='false'");
            output.WriteLine("  CrashImpacted='false'");

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