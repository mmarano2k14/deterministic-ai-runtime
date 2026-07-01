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
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios;
using Multiplexed.AI.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http
{
    /// <summary>
    /// Proves real HTTP process-host runtime crash recovery without synthetic DAG reseeding.
    /// </summary>
    public sealed class HttpProcessHostRealRuntimeCrashRecoveryScenarioTests
    {
        private const int StepCount = 100;
        private const int MultiTenantStepCount = 50;
        private const int KillAfterCompletedStepCount = 25;
        private const int FlakyStepIntervalMs = 500;
        private const string RequestedBy = "http-process-host-real-runtime-crash-recovery-test";
        private const string Source = "integration-test";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostRealRuntimeCrashRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostRealRuntimeCrashRecoveryScenarioTests(
            ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that a real runtime process crash is detected and the in-flight DAG execution resumes on a replacement runtime.
        /// </summary>
        /// <returns>A task that completes when the proof has finished.</returns>
        [Fact]
        public async Task Http_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill()
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
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
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
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-real-process-kill-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Starting. ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', Pipeline='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var dispatchedRun =
                await ProductionSharedRunTestHelpers
                    .SubmitAndDispatchOneRunAsync(
                        mcp,
                        scaleOutRequestStore,
                        tenant,
                        controlPlaneId,
                        pipelineName,
                        RequestedBy,
                        Source,
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

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Real run dispatched. SharedRunId='{dispatchedRunWithExecutionId.SharedRunId}', RuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}', SharedStoreExecutionId='{dispatchedRunWithExecutionId.ExecutionId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    executionId,
                    KillAfterCompletedStepCount,
                    TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Durable DAG progress reached. ExecutionId='{executionId}', CompletedSteps>='{KillAfterCompletedStepCount}'.");

            var killed =
                await processControl
                    .KillAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.True(
                killed,
                $"Runtime process was not killed. RuntimeInstanceId='{failedRuntimeInstanceId}'.");

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Runtime process killed. RuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForRuntimeInstanceUnsafeAsync(
                    registry,
                    failedRuntimeInstanceId,
                    TimeSpan.FromMinutes(2))
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Runtime instance automatically marked unsafe. RuntimeInstanceId='{failedRuntimeInstanceId}'.");

            var requeuedEntry =
                await ProductionRecoveryWaitHelpers
                    .WaitForRuntimeExecutionRequeuedAsync(
                        runExecutionIndex,
                        localRunId,
                        executionId,
                        TimeSpan.FromMinutes(1))
                    .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] In-flight execution requeued for recovery. FailedRuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{localRunId}', ExecutionId='{executionId}', IndexStatus='{requeuedEntry.Status}', IndexRuntimeInstanceId='{requeuedEntry.RuntimeInstanceId}'.");

            var redispatchedRun =
                await ProductionRecoveryWaitHelpers
                    .WaitForRecoveredRunRedispatchedAsync(
                        sharedRunStore,
                        dispatchedRunWithExecutionId.SharedRunId,
                        failedRuntimeInstanceId,
                        localRunId,
                        TimeSpan.FromMinutes(2))
                    .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Recovered shared run redispatched. SharedRunId='{redispatchedRun.SharedRunId}', NewRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', NewLocalRunId='{redispatchedRun.LocalRunId}', OriginalRuntimeInstanceId='{failedRuntimeInstanceId}', OriginalLocalRunId='{localRunId}'.");

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

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Recovered dispatch resolved durable execution. SharedRunId='{redispatchedRun.SharedRunId}', NewLocalRunId='{redispatchedRun.LocalRunId}', OriginalExecutionId='{executionId}', RecoveredExecutionId='{recoveredExecutionId}'.");

            Assert.False(string.IsNullOrWhiteSpace(recoveredExecutionId));
            Assert.Equal(executionId, recoveredExecutionId);

            this.output.WriteLine(
                "[REAL RUNTIME CRASH PROOF] Strict DAG resume validated. Runtime process crash recovered on a replacement runtime while preserving the original durable execution id. " +
                $"OriginalExecutionId='{executionId}', RecoveredExecutionId='{recoveredExecutionId}', OriginalRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    executionId,
                    StepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Recovered redispatch DAG execution completed all durable steps. RecoveredExecutionId='{recoveredExecutionId}', CompletedSteps='{StepCount}'.");
        }

        /// <summary>
        /// Verifies that two tenants can recover real process-host runtime crashes with strict DAG resume,
        /// forensics, replay, ledger, trace, inventory proof, and no cross-tenant recovery leak.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace()
        {
            var scenario =
                CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(3);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(7);

            var scenarioStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            var phaseStopwatch =
                System.Diagnostics.Stopwatch.StartNew();

            void WriteTiming(string phaseName)
            {
                this.output.WriteLine(
                    $"[REAL RUNTIME TWO-TENANT CRASH TIMING] Phase='{phaseName}', Duration='{phaseStopwatch.Elapsed}', TotalElapsed='{scenarioStopwatch.Elapsed}'.");

                phaseStopwatch.Restart();
            }

            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    scenario.ControlPlaneIdPrefix);

            var runtimeHostAssemblyPath =
                GenericMcpRuntimeHostAssemblyResolver.ResolveRuntimeHostAssemblyPath();

            var settings =
                HttpProcessHostProductionScenarioSettingsBuilder.Build(
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
                        RequestedBy,
                        tenantId: tenantA.TenantId,
                        tenantGroupId: tenantA.TenantGroupId)
                    .ConfigureAwait(false);

            var tenantBMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantBHttpClient,
                        RequestedBy,
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

            this.output.WriteLine(
                "# SCENARIO INTRO - REAL PROCESS-HOST TWO-TENANT CRASH RECOVERY");

            this.output.WriteLine(
                $"This test kills one real external runtime process per tenant, recovers in-flight DAG work with strict resume, recovers volatile local queued work by durable redispatch, then proves forensics, replay, ledger, trace, and tenant isolation. ControlPlaneId='{controlPlaneId}'.");

            this.output.WriteLine(
                $"[REAL RUNTIME TWO-TENANT CRASH PROOF] Starting. ControlPlaneId='{controlPlaneId}', TenantAPipelinePrefix='{tenantAPipelinePrefix}', TenantBPipelinePrefix='{tenantBPipelinePrefix}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            WriteTiming("Scenario identifiers and intro output");

            var tenantAInventoryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .SubmitAndBuildAssignedWorkInventoryAsync(
                        this.output,
                        tenantAMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        tenantA,
                        controlPlaneId,
                        tenantAPipelinePrefix,
                        RequestedBy,
                        Source,
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
                        this.output,
                        tenantBMcp,
                        scaleOutRequestStore,
                        sharedRunStore,
                        runExecutionIndex,
                        dagStore,
                        tenantB,
                        controlPlaneId,
                        tenantBPipelinePrefix,
                        RequestedBy,
                        Source,
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

            var tenantARecoveryTask =
                ProductionRealRuntimeCrashRecoveryTestHelpers
                    .KillRuntimeAndRecoverAssignedInventoryAsync(
                        this.output,
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
                        this.output,
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

            var recoveries =
                new[]
                {
            tenantARecovery,
            tenantBRecovery
                };

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH STEP 1 - MCP FORENSICS PROOF] Starting recovery forensics validation.");

            var tenantAForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        this.output,
                        forensicsQueryService,
                        tenantARecovery,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            var tenantBForensics =
                await ProductionRealRuntimeCrashRecoveryTestHelpers
                    .AssertRecoveredInventoryForensicsAsync(
                        this.output,
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

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH STEP 1 - MCP FORENSICS PROOF] " +
                $"TenantA='{tenantA.TenantId}', FailedRuntimeA='{tenantAInventory.RuntimeInstanceId}', ExpectedA='{tenantARecovery.RecoveredWorks.Count}', ActualA='{tenantAForensics.Count}', " +
                $"TenantB='{tenantB.TenantId}', FailedRuntimeB='{tenantBInventory.RuntimeInstanceId}', ExpectedB='{tenantBRecovery.RecoveredWorks.Count}', ActualB='{tenantBForensics.Count}', " +
                "CrossTenantLeakDetected='false', DuplicateRecoveryDetected='false'.");

            WriteTiming("Validate MCP runtime recovery forensics");

            ProductionRealRuntimeCrashRecoveryTestHelpers.AssertNoCrossTenantInventoryRecoveryLeak(
                recoveries);

            WriteTiming("Validate no cross-tenant inventory recovery leak");

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    this.output,
                    dagStore,
                    tenantARecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            await ProductionRealRuntimeCrashRecoveryTestHelpers
                .AssertRecoveredInventoryDagCompletedAsync(
                    this.output,
                    dagStore,
                    tenantBRecovery,
                    MultiTenantStepCount,
                    scenario.CompletionTimeout)
                .ConfigureAwait(false);

            WriteTiming("Wait for recovered DAG completion");

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

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH STEP 2 - MCP REPLAY TRACE PROOF] Starting replay, ledger evidence, and trace evidence validation for recovered executions.");

            var tenantAReplayProofs =
                await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                    .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                        tenantAMcp,
                        tenantA.TenantId,
                        tenantAFinalStatuses,
                        RequestedBy,
                        Source)
                    .ConfigureAwait(false);

            var tenantBReplayProofs =
                await HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
                    .AssertRecoveredExecutionsReplayableThroughMcpAsync(
                        tenantBMcp,
                        tenantB.TenantId,
                        tenantBFinalStatuses,
                        RequestedBy,
                        Source)
                    .ConfigureAwait(false);

            ProductionRuntimeReplayOutput.WriteRecoveredExecutionReplayProof(
                this.output,
                tenantAReplayProofs,
                tenantBReplayProofs);

            WriteTiming("Validate MCP replay ledger and trace proof");

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH STEP 3 - MCP LEDGER PROOF] Querying tenant-scoped MCP ledger evidence.");

            var tenantALedgerEntries =
                await tenantAMcp
                    .QueryLedgerAsync(
                        new AiDecisionLedgerQuery
                        {
                            TimestampFromUtc = ledgerTimelineFromUtc,
                            Limit = 10000
                        })
                    .ConfigureAwait(false);

            var tenantBLedgerEntries =
                await tenantBMcp
                    .QueryLedgerAsync(
                        new AiDecisionLedgerQuery
                        {
                            TimestampFromUtc = ledgerTimelineFromUtc,
                            Limit = 10000
                        })
                    .ConfigureAwait(false);

            var ledgerEntries =
                tenantALedgerEntries
                    .Concat(tenantBLedgerEntries)
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

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH STEP 3 - MCP LEDGER PROOF] " +
                $"TenantAEntries='{tenantALedgerEntries.Count}', TenantBEntries='{tenantBLedgerEntries.Count}', CombinedEntries='{ledgerEntries.Length}'.");

            WriteTiming("Query and validate tenant scoped MCP ledger");

            WriteTiming("Scenario finalization");

            this.output.WriteLine(
                "[REAL RUNTIME TWO-TENANT CRASH FINAL PROOF] " +
                $"ControlPlaneId='{controlPlaneId}', " +
                $"TotalElapsed='{scenarioStopwatch.Elapsed}', " +
                $"TenantA='{tenantA.TenantId}', FailedRuntimeA='{tenantAInventory.RuntimeInstanceId}', RecoveredA='{tenantARecovery.RecoveredWorks.Count}', ForensicsA='{tenantAForensics.Count}', ReplayProofA='{tenantAReplayProofs.Count}', " +
                $"TenantB='{tenantB.TenantId}', FailedRuntimeB='{tenantBInventory.RuntimeInstanceId}', RecoveredB='{tenantBRecovery.RecoveredWorks.Count}', ForensicsB='{tenantBForensics.Count}', ReplayProofB='{tenantBReplayProofs.Count}', " +
                $"ForensicsValidated='true', StrictResumeValidated='true', ReplayValidated='true', LedgerValidated='true', TraceValidated='true', CrossTenantLeakDetected='false', DuplicateRecoveryDetected='false'.");
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

        private static ProductionRuntimeScenarioDefinition CreateRealRuntimeCrashRecoveryTwoTenantInventoryScenario()
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

            return baseScenario with
            {
                Name = "http-process-host-real-runtime-crash-recovery-two-tenant-inventory",
                ControlPlaneIdPrefix = "http-process-host-real-runtime-crash-recovery-two-tenant-inventory",
                Tenants = new[]
                {
                    tenantA,
                    tenantB
                },
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
    }
}