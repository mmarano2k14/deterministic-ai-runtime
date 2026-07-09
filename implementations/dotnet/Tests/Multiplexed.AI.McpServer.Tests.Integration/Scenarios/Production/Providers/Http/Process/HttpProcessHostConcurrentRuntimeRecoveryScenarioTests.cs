using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Context;
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
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Runners;
using Multiplexed.AI.Stores;
using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.Process
{
    /// <summary>
    /// HTTP process-host concurrent runtime recovery tests proving that simultaneous
    /// multi-instance failures recover isolated work inventories without cross-tenant,
    /// cross-incident, duplicate, or self-redispatch corruption.
    /// </summary>
    public sealed class HttpProcessHostConcurrentRuntimeRecoveryScenarioTests
    {
        private const string RequestedBy = "http-process-host-concurrent-runtime-recovery-test";
        private const string Source = "http-process-host-concurrent-runtime-recovery";
        private const int StepCount = 100;
        private const int FailureStepNumber = 50;
        private const int TenantAFailedWorkCount = 3;
        private const int TenantBFailedWorkCount = 2;

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostConcurrentRuntimeRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostConcurrentRuntimeRecoveryScenarioTests(
            ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that concurrent HTTP runtime instance failures recover isolated assigned-work inventories
        /// without cross-tenant leak, cross-incident leak, duplicate recovery, or self-redispatch.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Concurrent_MultiInstance_Failures_Without_CrossTenant_Or_CrossIncident_Leak()
        {
            var scenario =
                CreateConcurrentMultiInstanceRecoveryScenario();

            ProductionRuntimeScenarioSummaryOutput.WriteConcurrentMultiInstanceRecoveryIntro(this.output, scenario);

            scenario.DispatchTimeout = TimeSpan.FromMinutes(2);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(5);

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

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var healthReconciler =
                host.Services.GetRequiredService<IAiRuntimeInstanceHealthReconciler>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                host.Services.GetRequiredService<IAiSharedQueue>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var sharedQueueDispatcher =
                host.Services.GetRequiredService<IAiSharedQueueDispatcher>();

            var queryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            var ledger = host.Services.GetRequiredService<CapturingIntegrationDecisionLedgerRecorder>();
            ledger.Clear();

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenantA =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-a", StringComparison.Ordinal));

            var tenantB =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-b", StringComparison.Ordinal));

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

            var tenantAPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            var tenantAControlPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-control-runtime-{Guid.NewGuid():N}";

            var tenantBPipelineName =
                $"{scenario.Name}-{tenantB.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[MULTI-INSTANCE RECOVERY] Starting. ControlPlaneId='{controlPlaneId}', TenantAPipeline='{tenantAPipelineName}', TenantAControlPipeline='{tenantAControlPipelineName}', TenantBPipeline='{tenantBPipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var tenantAFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantAMcp,
                        scaleOutRequestStore,
                        tenantA,
                        controlPlaneId,
                        tenantAPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantAControlRuntimeInstanceId =
                await PublishScaleOutAndWaitForAdditionalTenantRuntimeInstanceAsync(
                        scaleOutPublisher,
                        registry,
                        tenantAFailedBootstrap,
                        tenantA,
                        controlPlaneId,
                        tenantAControlPipelineName,
                        tenantAFailedBootstrap.AssignedRuntimeInstanceId!,
                        scenario.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var tenantBFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantBMcp,
                        scaleOutRequestStore,
                        tenantB,
                        controlPlaneId,
                        tenantBPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.LocalRunId));

            var tenantAFailedRuntimeInstanceId =
                tenantAFailedBootstrap.AssignedRuntimeInstanceId!;

            var tenantBFailedRuntimeInstanceId =
                tenantBFailedBootstrap.AssignedRuntimeInstanceId!;

            this.output.WriteLine(
                "[MULTI-INSTANCE RUNTIME SELECTION] " +
                $"TenantAFailedRuntime='{tenantAFailedRuntimeInstanceId}', " +
                $"TenantAControlRuntime='{tenantAControlRuntimeInstanceId}', " +
                $"TenantBFailedRuntime='{tenantBFailedRuntimeInstanceId}'.");

            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantAControlRuntimeInstanceId);
            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantBFailedRuntimeInstanceId);
            Assert.NotEqual(tenantAControlRuntimeInstanceId, tenantBFailedRuntimeInstanceId);

            AssertRuntimeBelongsToTenant(tenantAFailedRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantAControlRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantBFailedRuntimeInstanceId, tenantB);

            var tenantASeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantAFailedBootstrap,
                        tenantA,
                        tenantAPipelineName,
                        tenantAFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantAFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantBSeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantBFailedBootstrap,
                        tenantB,
                        tenantBPipelineName,
                        tenantBFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantBFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantAGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantA,
                    FailedRuntimeInstanceId = tenantAFailedRuntimeInstanceId,
                    SeededWorks = tenantASeededWorks
                };

            var tenantBGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantB,
                    FailedRuntimeInstanceId = tenantBFailedRuntimeInstanceId,
                    SeededWorks = tenantBSeededWorks
                };

            var failedRuntimeGroups =
                new[]
                {
                    tenantAGroup,
                    tenantBGroup
                };

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks);

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks);

            await WaitForSeededRuntimeGroupsVisibleAsync(
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            await MarkUnhealthyAndReconcileUntilAllRuntimeGroupsRecoveredAsync(
                    registry,
                    healthReconciler,
                    recoveryReconciler,
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(120))
                .ConfigureAwait(false);

            await AssertControlRuntimeUntouchedBeforeRedispatchAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId)
                .ConfigureAwait(false);

            var tenantARedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantAGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantBRedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantBGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var allRedispatchedRuns =
                tenantARedispatchedRuns
                    .Concat(tenantBRedispatchedRuns)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allRedispatchedRuns.Length);

            AssertNoSelfRedispatch(
                failedRuntimeGroups,
                allRedispatchedRuns);

            AssertNoCrossTenantRedispatch(
                tenantAGroup,
                tenantARedispatchedRuns,
                tenantBGroup,
                tenantBRedispatchedRuns);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks,
                tenantARedispatchedRuns);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks,
                tenantBRedispatchedRuns);

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

            AssertAllRecoveredRunsCompleted(tenantAFinalStatuses);
            AssertAllRecoveredRunsCompleted(tenantBFinalStatuses);

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantAGroup,
                    tenantARedispatchedRuns)
                .ConfigureAwait(false);

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantBGroup,
                    tenantBRedispatchedRuns)
                .ConfigureAwait(false);

            var tenantAForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantAGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var tenantBForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantBGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var allForensics =
                tenantAForensics
                    .Concat(tenantBForensics)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allForensics.Length);

            AssertNoDuplicateForensics(allForensics);
            AssertNoCrossTenantForensicsLeak(tenantAGroup, tenantAForensics, tenantBGroup, tenantBForensics);
            AssertNoCrossIncidentForensicsLeak(tenantAForensics, tenantBForensics);

            await AssertControlRuntimeUntouchedAfterRecoveryAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId,
                    allForensics)
                .ConfigureAwait(false);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantAForensics);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBForensics);

            this.output.WriteLine(
                "[MULTI-INSTANCE RECOVERY PROOF] " +
                $"RuntimeA='{tenantAFailedRuntimeInstanceId}' -> '{tenantARedispatchedRuns.Count}/{tenantASeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantARedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"RuntimeB='{tenantBFailedRuntimeInstanceId}' -> '{tenantBRedispatchedRuns.Count}/{tenantBSeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantBRedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"ControlRuntime='{tenantAControlRuntimeInstanceId}' -> untouched, 0 forensics events, " +
                $"ExpectedForensics='{CountSeededWork(failedRuntimeGroups)}', ActualForensics='{allForensics.Length}', " +
                $"CrossTenantLeakDetected='false', CrossIncidentLeakDetected='false', DuplicateRecoveryDetected='false', SelfRedispatchDetected='false'.");

            var ledgerRecords = ledger.Records;

            ProductionControlPlaneLedgerProofAssertions.AssertScaleOutAndRuntimeVisibilityProof(ledgerRecords);
            ProductionControlPlaneLedgerProofAssertions.AssertConcurrentRecoveryProof(ledgerRecords);
            ProductionControlPlaneLedgerProofAssertions.AssertContainsTenant(ledgerRecords, tenantA.TenantId);
            ProductionControlPlaneLedgerProofAssertions.AssertContainsTenant(ledgerRecords, tenantB.TenantId);

            ProductionControlPlaneLedgerProofOutput.WriteConcurrentRuntimeRecoveryProof(
                this.output,
                ledgerRecords,
                new ProductionControlPlaneLedgerProofContext
                {
                    ControlPlaneId = controlPlaneId,
                    TenantAId = tenantA.TenantId,
                    TenantBId = tenantB.TenantId,
                    TenantAFailedRuntimeInstanceId = tenantAFailedRuntimeInstanceId,
                    TenantBFailedRuntimeInstanceId = tenantBFailedRuntimeInstanceId,
                    ControlRuntimeInstanceId = tenantAControlRuntimeInstanceId,
                    ExpectedRecoveredWorkCount = CountSeededWork(failedRuntimeGroups),
                    RecoveredWorkCount = allRedispatchedRuns.Length,
                    CrossTenantLeakDetected = false,
                    CrossIncidentLeakDetected = false,
                    DuplicateRecoveryDetected = false,
                    SelfRedispatchDetected = false
                });
        }

        /// <summary>
        /// Verifies that concurrent HTTP runtime instance failures recover isolated assigned-work inventories
        /// without cross-tenant leak, cross-incident leak, duplicate recovery, or self-redispatch,
        /// and then proves the resulting control-plane ledger through the existing MCP observability.ledger.query tool.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Concurrent_MultiInstance_Failures_Without_CrossTenant_Or_CrossIncident_Leak_MCP_Ledger()
        {
            var scenario =
                CreateConcurrentMultiInstanceRecoveryScenario();

            ProductionRuntimeScenarioSummaryOutput.WriteConcurrentMultiInstanceRecoveryIntro(this.output, scenario);

            scenario.DispatchTimeout = TimeSpan.FromMinutes(2);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(5);

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

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var healthReconciler =
                host.Services.GetRequiredService<IAiRuntimeInstanceHealthReconciler>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                host.Services.GetRequiredService<IAiSharedQueue>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var sharedQueueDispatcher =
                host.Services.GetRequiredService<IAiSharedQueueDispatcher>();

            var queryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenantA =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-a", StringComparison.Ordinal));

            var tenantB =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-b", StringComparison.Ordinal));

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

            var ledgerTimelineFromUtc =
                DateTimeOffset.UtcNow.AddSeconds(-5);

            var tenantAPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            var tenantAControlPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-control-runtime-{Guid.NewGuid():N}";

            var tenantBPipelineName =
                $"{scenario.Name}-{tenantB.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[MULTI-INSTANCE RECOVERY] Starting. ControlPlaneId='{controlPlaneId}', TenantAPipeline='{tenantAPipelineName}', TenantAControlPipeline='{tenantAControlPipelineName}', TenantBPipeline='{tenantBPipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var tenantAFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantAMcp,
                        scaleOutRequestStore,
                        tenantA,
                        controlPlaneId,
                        tenantAPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantAControlRuntimeInstanceId =
                await PublishScaleOutAndWaitForAdditionalTenantRuntimeInstanceAsync(
                        scaleOutPublisher,
                        registry,
                        tenantAFailedBootstrap,
                        tenantA,
                        controlPlaneId,
                        tenantAControlPipelineName,
                        tenantAFailedBootstrap.AssignedRuntimeInstanceId!,
                        scenario.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var tenantBFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantBMcp,
                        scaleOutRequestStore,
                        tenantB,
                        controlPlaneId,
                        tenantBPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.LocalRunId));

            var tenantAFailedRuntimeInstanceId =
                tenantAFailedBootstrap.AssignedRuntimeInstanceId!;

            var tenantBFailedRuntimeInstanceId =
                tenantBFailedBootstrap.AssignedRuntimeInstanceId!;

            this.output.WriteLine(
                "[MULTI-INSTANCE RUNTIME SELECTION] " +
                $"TenantAFailedRuntime='{tenantAFailedRuntimeInstanceId}', " +
                $"TenantAControlRuntime='{tenantAControlRuntimeInstanceId}', " +
                $"TenantBFailedRuntime='{tenantBFailedRuntimeInstanceId}'.");

            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantAControlRuntimeInstanceId);
            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantBFailedRuntimeInstanceId);
            Assert.NotEqual(tenantAControlRuntimeInstanceId, tenantBFailedRuntimeInstanceId);

            AssertRuntimeBelongsToTenant(tenantAFailedRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantAControlRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantBFailedRuntimeInstanceId, tenantB);

            var tenantASeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantAFailedBootstrap,
                        tenantA,
                        tenantAPipelineName,
                        tenantAFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantAFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantBSeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantBFailedBootstrap,
                        tenantB,
                        tenantBPipelineName,
                        tenantBFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantBFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantAGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantA,
                    FailedRuntimeInstanceId = tenantAFailedRuntimeInstanceId,
                    SeededWorks = tenantASeededWorks
                };

            var tenantBGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantB,
                    FailedRuntimeInstanceId = tenantBFailedRuntimeInstanceId,
                    SeededWorks = tenantBSeededWorks
                };

            var failedRuntimeGroups =
                new[]
                {
            tenantAGroup,
            tenantBGroup
                };

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks);

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks);

            await WaitForSeededRuntimeGroupsVisibleAsync(
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            await MarkUnhealthyAndReconcileUntilAllRuntimeGroupsRecoveredAsync(
                    registry,
                    healthReconciler,
                    recoveryReconciler,
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(120))
                .ConfigureAwait(false);

            await AssertControlRuntimeUntouchedBeforeRedispatchAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId)
                .ConfigureAwait(false);

            var tenantARedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantAGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantBRedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantBGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var allRedispatchedRuns =
                tenantARedispatchedRuns
                    .Concat(tenantBRedispatchedRuns)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allRedispatchedRuns.Length);

            AssertNoSelfRedispatch(
                failedRuntimeGroups,
                allRedispatchedRuns);

            AssertNoCrossTenantRedispatch(
                tenantAGroup,
                tenantARedispatchedRuns,
                tenantBGroup,
                tenantBRedispatchedRuns);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks,
                tenantARedispatchedRuns);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks,
                tenantBRedispatchedRuns);

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

            AssertAllRecoveredRunsCompleted(tenantAFinalStatuses);
            AssertAllRecoveredRunsCompleted(tenantBFinalStatuses);


           

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantAGroup,
                    tenantARedispatchedRuns)
                .ConfigureAwait(false);

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantBGroup,
                    tenantBRedispatchedRuns)
                .ConfigureAwait(false);

            var tenantAForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantAGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var tenantBForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantBGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var allForensics =
                tenantAForensics
                    .Concat(tenantBForensics)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allForensics.Length);

            AssertNoDuplicateForensics(allForensics);
            AssertNoCrossTenantForensicsLeak(tenantAGroup, tenantAForensics, tenantBGroup, tenantBForensics);
            AssertNoCrossIncidentForensicsLeak(tenantAForensics, tenantBForensics);

            await AssertControlRuntimeUntouchedAfterRecoveryAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId,
                    allForensics)
                .ConfigureAwait(false);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantAForensics);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBForensics);

            this.output.WriteLine(
                "[MULTI-INSTANCE RECOVERY PROOF] " +
                $"RuntimeA='{tenantAFailedRuntimeInstanceId}' -> '{tenantARedispatchedRuns.Count}/{tenantASeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantARedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"RuntimeB='{tenantBFailedRuntimeInstanceId}' -> '{tenantBRedispatchedRuns.Count}/{tenantBSeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantBRedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"ControlRuntime='{tenantAControlRuntimeInstanceId}' -> untouched, 0 forensics events, " +
                $"ExpectedForensics='{CountSeededWork(failedRuntimeGroups)}', ActualForensics='{allForensics.Length}', " +
                $"CrossTenantLeakDetected='false', CrossIncidentLeakDetected='false', DuplicateRecoveryDetected='false', SelfRedispatchDetected='false'.");

            var tenantAReplayProofs = await
           AssertRecoveredExecutionsReplayableThroughMcpAsync(
               tenantAMcp,
               tenantA.TenantId,
               tenantAFinalStatuses,
               RequestedBy,
               Source)
            .ConfigureAwait(false);

            var tenantBReplayProofs = await
            AssertRecoveredExecutionsReplayableThroughMcpAsync(
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

            var ledgerRecords =
                await QueryControlPlaneLedgerRecordsThroughMcpAsync(
                        tenantAMcp,
                        tenantBMcp,
                        ledgerTimelineFromUtc,
                        controlPlaneId,
                        tenantA,
                        tenantB,
                        tenantAPipelineName,
                        tenantAControlPipelineName,
                        tenantBPipelineName,
                        tenantAFailedRuntimeInstanceId,
                        tenantAControlRuntimeInstanceId,
                        tenantBFailedRuntimeInstanceId)
                    .ConfigureAwait(false);

            this.output.WriteLine(
                $"[MCP LEDGER QUERY PROOF] Queried ledger through MCP tool 'observability.ledger.query'. " +
                $"ScenarioEntries='{ledgerRecords.Count}', " +
                $"ControlPlaneEntries='{ledgerRecords.Count(record => record.EventType.StartsWith("control.", StringComparison.Ordinal))}'.");

            Assert.Contains(
                ledgerRecords,
                record => record.EventType.StartsWith("control.", StringComparison.Ordinal));

            ProductionControlPlaneLedgerProofAssertions.AssertScaleOutAndRuntimeVisibilityProof(ledgerRecords);
            ProductionControlPlaneLedgerProofAssertions.AssertConcurrentRecoveryProof(ledgerRecords);
            ProductionControlPlaneLedgerProofAssertions.AssertContainsTenant(ledgerRecords, tenantA.TenantId);
            ProductionControlPlaneLedgerProofAssertions.AssertContainsTenant(ledgerRecords, tenantB.TenantId);

            ProductionControlPlaneLedgerProofOutput.WriteConcurrentRuntimeRecoveryProof(
                this.output,
                ledgerRecords,
                new ProductionControlPlaneLedgerProofContext
                {
                    ControlPlaneId = controlPlaneId,
                    TenantAId = tenantA.TenantId,
                    TenantBId = tenantB.TenantId,
                    TenantAFailedRuntimeInstanceId = tenantAFailedRuntimeInstanceId,
                    TenantBFailedRuntimeInstanceId = tenantBFailedRuntimeInstanceId,
                    ControlRuntimeInstanceId = tenantAControlRuntimeInstanceId,
                    ExpectedRecoveredWorkCount = CountSeededWork(failedRuntimeGroups),
                    RecoveredWorkCount = allRedispatchedRuns.Length,
                    CrossTenantLeakDetected = false,
                    CrossIncidentLeakDetected = false,
                    DuplicateRecoveryDetected = false,
                    SelfRedispatchDetected = false
                });

            ProductionControlPlaneLedgerTenantOutput.WriteTenantLedgerSummary(
                this.output,
                ledgerRecords,
                new[]
                {
                    tenantA.TenantId,
                    tenantB.TenantId
                },
                maxEventsPerTenant: 300);
        }


        /// <summary>
        /// Verifies that recovered executions can be replayed through MCP and that their execution evidence
        /// is available through MCP ledger and trace APIs.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="finalStatuses">The recovered terminal runtime statuses.</param>
        /// <param name="requestedBy">The requested-by value.</param>
        /// <param name="source">The source value.</param>
        /// <returns>The recovered execution replay proof records.</returns>
        public static async Task<IReadOnlyCollection<RecoveredExecutionReplayProofRecord>> AssertRecoveredExecutionsReplayableThroughMcpAsync(
            McpTestClient mcp,
            string tenantId,
            IReadOnlyCollection<AiRuntimeQueueControlPlaneResult> finalStatuses,
            string requestedBy,
            string source)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
            ArgumentNullException.ThrowIfNull(finalStatuses);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            var results =
                new List<RecoveredExecutionReplayProofRecord>();

            foreach (var status in finalStatuses)
            {
                var executionId =
                    status.ExecutionId ??
                    status.RunState?.ExecutionId;

                Assert.False(
                    string.IsNullOrWhiteSpace(executionId),
                    $"Recovered runtime status did not expose an execution id. TenantId='{tenantId}', RuntimeInstanceId='{status.RuntimeInstanceId}', RunId='{status.RunId}', Status='{status.RunState?.Status}'.");

                var replayRequest =
                    new AiReplayControlRequest
                    {
                        ExecutionId = executionId!,
                        CorrelationId = $"recovered-execution-replay-{Guid.NewGuid():N}",
                        RequestedBy = requestedBy,
                        Source = source,
                        Operation = AiReplayOperation.Replay
                    };

                var replayResult =
                    await mcp.ReplayExecutionAsync(replayRequest)
                        .ConfigureAwait(false);

                var replayFailureReason =
                    replayResult.FailureReason ??
                    replayResult.Message;

                var isSyntheticRecoveredExecution =
                    executionId.StartsWith(
                        "http-runtime-inventory-running-execution-",
                        StringComparison.Ordinal);

                /*
                if (!isSyntheticRecoveredExecution)
                {
                    Assert.True(
                        replayResult.Success,
                        replayFailureReason);
                }
                */


                Assert.True(
                    replayResult.Success,
                    $"Recovered execution is not replayable through MCP. TenantId='{tenantId}', ExecutionId='{executionId}', RuntimeInstanceId='{status.RuntimeInstanceId}', RunId='{status.RunId}', Failure='{replayFailureReason}'.");

                replayRequest.Operation =
                    AiReplayOperation.GetReport;

                var replayReport =
                    await mcp.GetReplayReportAsync(replayRequest)
                        .ConfigureAwait(false);

                replayRequest.Operation =
                    AiReplayOperation.GetLedger;

                var replayLedger =
                    await mcp.GetReplayLedgerAsync(replayRequest)
                        .ConfigureAwait(false);

                replayRequest.Operation =
                    AiReplayOperation.GetTimeline;

                var replayTrace =
                    await mcp.GetReplayTraceAsync(replayRequest)
                        .ConfigureAwait(false);

                var executionLedger =
                    await mcp.GetLedgerByExecutionAsync(executionId!)
                        .ConfigureAwait(false);

                Assert.NotEmpty(executionLedger);

                var hasCompletionEvidence =
                    executionLedger.Any(entry =>
                        string.Equals(entry.EventType, "execution.completed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(entry.EventType, "finalization.completed", StringComparison.OrdinalIgnoreCase));

                var hasStepCompletionEvidence =
                    executionLedger.Any(entry =>
                        string.Equals(entry.EventType, "step.completed", StringComparison.OrdinalIgnoreCase));

                Assert.True(
                    hasCompletionEvidence,
                    $"Recovered execution has no completion ledger evidence. TenantId='{tenantId}', ExecutionId='{executionId}'.");

                Assert.True(
                    hasStepCompletionEvidence,
                    $"Recovered execution has no step completion ledger evidence. TenantId='{tenantId}', ExecutionId='{executionId}'.");

                var executionTrace =
                    await mcp.GetTraceByExecutionAsync(executionId!)
                        .ConfigureAwait(false);

                Assert.NotEmpty(executionTrace);

                results.Add(
                    new RecoveredExecutionReplayProofRecord
                    {
                        TenantId = tenantId,
                        RuntimeInstanceId = status.RuntimeInstanceId,
                        LocalRunId = status.RunId,
                        ExecutionId = executionId!,
                        ReplaySucceeded = replayResult.Success,
                        ReplayFailureReason = replayFailureReason,
                        SyntheticRecoveredExecution = isSyntheticRecoveredExecution,
                        ReplayReportAvailable = replayReport.Success,
                        ReplayLedgerAvailable = replayLedger.Success,
                        ReplayTraceAvailable = replayTrace.Success,
                        ExecutionLedgerAvailable = executionLedger.Count > 0,
                        ExecutionTraceAvailable = executionTrace.Count > 0,
                        CompletionLedgerEvidenceAvailable = hasCompletionEvidence,
                        StepCompletionLedgerEvidenceAvailable = hasStepCompletionEvidence
                    });
            }

            return results;
        }

        /// <summary>
        /// Queries control-plane ledger records through the existing MCP observability ledger query tool and converts them to the assertion/output shape.
        /// </summary>
        /// <param name="tenantAMcp">The tenant A MCP client.</param>
        /// <param name="tenantBMcp">The tenant B MCP client.</param>
        /// <param name="timestampFromUtc">The inclusive lower timestamp bound.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantA">Tenant A.</param>
        /// <param name="tenantB">Tenant B.</param>
        /// <param name="tenantAPipelineName">The tenant A workload pipeline name.</param>
        /// <param name="tenantAControlPipelineName">The tenant A control-runtime pipeline name.</param>
        /// <param name="tenantBPipelineName">The tenant B workload pipeline name.</param>
        /// <param name="tenantAFailedRuntimeInstanceId">The tenant A failed runtime instance identifier.</param>
        /// <param name="tenantAControlRuntimeInstanceId">The tenant A control runtime instance identifier.</param>
        /// <param name="tenantBFailedRuntimeInstanceId">The tenant B failed runtime instance identifier.</param>
        /// <returns>The scenario ledger records queried through MCP.</returns>
        private async Task<IReadOnlyCollection<CapturedIntegrationLedgerRecord>> QueryControlPlaneLedgerRecordsThroughMcpAsync(
            McpTestClient tenantAMcp,
            McpTestClient tenantBMcp,
            DateTimeOffset timestampFromUtc,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenantA,
            ProductionTenantScenarioDefinition tenantB,
            string tenantAPipelineName,
            string tenantAControlPipelineName,
            string tenantBPipelineName,
            string tenantAFailedRuntimeInstanceId,
            string tenantAControlRuntimeInstanceId,
            string tenantBFailedRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(tenantAMcp);
            ArgumentNullException.ThrowIfNull(tenantBMcp);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(tenantA);
            ArgumentNullException.ThrowIfNull(tenantB);

            var query =
                new AiDecisionLedgerQuery
                {
                    TimestampFromUtc = timestampFromUtc,
                    Limit = 10000
                };

            var tenantAEntries =
                await tenantAMcp
                    .QueryLedgerAsync(query)
                    .ConfigureAwait(false);

            var tenantBEntries =
                await tenantBMcp
                    .QueryLedgerAsync(query)
                    .ConfigureAwait(false);

            var entries =
                tenantAEntries
                    .Concat(tenantBEntries)
                    .Where(entry =>
                        IsScenarioLedgerEntry(
                            entry,
                            controlPlaneId,
                            tenantA,
                            tenantB,
                            tenantAPipelineName,
                            tenantAControlPipelineName,
                            tenantBPipelineName,
                            tenantAFailedRuntimeInstanceId,
                            tenantAControlRuntimeInstanceId,
                            tenantBFailedRuntimeInstanceId))
                    .GroupBy(
                        CreateDecisionLedgerEntryDeduplicationKey,
                        StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(entry => entry.TimestampUtc)
                    .Select(ConvertDecisionLedgerEntryToCapturedRecord)
                    .ToArray();

            this.output.WriteLine(
                $"[MCP LEDGER QUERY PROOF] Queried ledger through MCP tool 'observability.ledger.query'. TenantAEntries='{tenantAEntries.Count}', TenantBEntries='{tenantBEntries.Count}', ScenarioEntries='{entries.Length}'.");

            return entries;
        }

        /// <summary>
        /// Determines whether a durable ledger entry belongs to the current scenario.
        /// </summary>
        /// <param name="entry">The ledger entry.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="tenantA">Tenant A.</param>
        /// <param name="tenantB">Tenant B.</param>
        /// <param name="tenantAPipelineName">The tenant A workload pipeline name.</param>
        /// <param name="tenantAControlPipelineName">The tenant A control-runtime pipeline name.</param>
        /// <param name="tenantBPipelineName">The tenant B workload pipeline name.</param>
        /// <param name="tenantAFailedRuntimeInstanceId">The tenant A failed runtime instance identifier.</param>
        /// <param name="tenantAControlRuntimeInstanceId">The tenant A control runtime instance identifier.</param>
        /// <param name="tenantBFailedRuntimeInstanceId">The tenant B failed runtime instance identifier.</param>
        /// <returns><c>true</c> when the entry belongs to the scenario; otherwise, <c>false</c>.</returns>
        private static bool IsScenarioLedgerEntry(
            AiDecisionLedgerEntry entry,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenantA,
            ProductionTenantScenarioDefinition tenantB,
            string tenantAPipelineName,
            string tenantAControlPipelineName,
            string tenantBPipelineName,
            string tenantAFailedRuntimeInstanceId,
            string tenantAControlRuntimeInstanceId,
            string tenantBFailedRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (MetadataEquals(entry, "controlPlaneId", controlPlaneId) ||
                MetadataEquals(entry, "control.plane.id", controlPlaneId))
            {
                return true;
            }

            if (MetadataEquals(entry, "tenantId", tenantA.TenantId) ||
                MetadataEquals(entry, "tenant.id", tenantA.TenantId) ||
                MetadataEquals(entry, "tenantId", tenantB.TenantId) ||
                MetadataEquals(entry, "tenant.id", tenantB.TenantId))
            {
                return true;
            }

            if (MetadataEquals(entry, "pipelineKey", tenantAPipelineName) ||
                MetadataEquals(entry, "pipeline.key", tenantAPipelineName) ||
                MetadataEquals(entry, "pipelineKey", tenantAControlPipelineName) ||
                MetadataEquals(entry, "pipeline.key", tenantAControlPipelineName) ||
                MetadataEquals(entry, "pipelineKey", tenantBPipelineName) ||
                MetadataEquals(entry, "pipeline.key", tenantBPipelineName))
            {
                return true;
            }

            var context =
                entry.CorrelationContext;

            if (string.Equals(context.RuntimeInstanceId, tenantAFailedRuntimeInstanceId, StringComparison.Ordinal) ||
                string.Equals(context.RuntimeInstanceId, tenantAControlRuntimeInstanceId, StringComparison.Ordinal) ||
                string.Equals(context.RuntimeInstanceId, tenantBFailedRuntimeInstanceId, StringComparison.Ordinal))
            {
                return true;
            }

            if (MetadataEquals(entry, "runtimeInstanceId", tenantAFailedRuntimeInstanceId) ||
                MetadataEquals(entry, "runtime.instance.id", tenantAFailedRuntimeInstanceId) ||
                MetadataEquals(entry, "runtimeInstanceId", tenantAControlRuntimeInstanceId) ||
                MetadataEquals(entry, "runtime.instance.id", tenantAControlRuntimeInstanceId) ||
                MetadataEquals(entry, "runtimeInstanceId", tenantBFailedRuntimeInstanceId) ||
                MetadataEquals(entry, "runtime.instance.id", tenantBFailedRuntimeInstanceId))
            {
                return true;
            }

            return IsControlPlaneInfrastructureLedgerEntry(entry);
        }

        /// <summary>
        /// Determines whether the entry is a control-plane infrastructure event that should be included in the scenario proof.
        /// </summary>
        /// <param name="entry">The ledger entry.</param>
        /// <returns><c>true</c> when the entry is an infrastructure proof event; otherwise, <c>false</c>.</returns>
        private static bool IsControlPlaneInfrastructureLedgerEntry(
            AiDecisionLedgerEntry entry)
        {
            return entry.EventType.Contains("runtime-instance-list", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-get", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-capacity-get", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-capacity-publish", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-instance-mark-unhealthy", StringComparison.Ordinal) ||
                entry.EventType.Contains("runtime-execution-recovery-reconcile", StringComparison.Ordinal) ||
                entry.EventType.Contains("shared-queue-pump-cycle", StringComparison.Ordinal);
        }

        /// <summary>
        /// Converts a durable decision ledger entry into the captured integration ledger shape.
        /// </summary>
        /// <param name="entry">The durable ledger entry.</param>
        /// <returns>The captured integration ledger record.</returns>
        private static CapturedIntegrationLedgerRecord ConvertDecisionLedgerEntryToCapturedRecord(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            var metadata =
                NormalizeLedgerMetadata(entry.Metadata);

            return new CapturedIntegrationLedgerRecord(
                entry.TimestampUtc,
                entry.CorrelationContext,
                entry.Category,
                entry.EventType,
                entry.Outcome,
                entry.Reason,
                metadata);
        }

        /// <summary>
        /// Normalizes ledger metadata so proof output can safely read string values.
        /// </summary>
        /// <param name="metadata">The ledger metadata.</param>
        /// <returns>The normalized metadata.</returns>
        private static IReadOnlyDictionary<string, string?> NormalizeLedgerMetadata(
            IReadOnlyDictionary<string, string?>? metadata)
        {
            if (metadata is null)
            {
                return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, string?>(metadata, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a stable deduplication key for durable ledger entries returned by multiple MCP tenant clients.
        /// </summary>
        /// <param name="entry">The ledger entry.</param>
        /// <returns>The deduplication key.</returns>
        private static string CreateDecisionLedgerEntryDeduplicationKey(
            AiDecisionLedgerEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.EntryId))
            {
                return entry.EntryId;
            }

            return string.Join(
                "|",
                entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                entry.Sequence.ToString(CultureInfo.InvariantCulture),
                entry.EventType ?? string.Empty,
                entry.Outcome.ToString(),
                entry.CorrelationContext.ExecutionId ?? string.Empty,
                entry.CorrelationContext.RunId ?? string.Empty,
                entry.CorrelationContext.RuntimeInstanceId ?? string.Empty);
        }

        /// <summary>
        /// Determines whether a metadata value equals the expected value.
        /// </summary>
        /// <param name="entry">The ledger entry.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="expectedValue">The expected value.</param>
        /// <returns><c>true</c> when the metadata value equals the expected value; otherwise, <c>false</c>.</returns>
        private static bool MetadataEquals(
            AiDecisionLedgerEntry entry,
            string key,
            string expectedValue)
        {
            return TryGetMetadataValue(entry, key, out var value) &&
                string.Equals(value, expectedValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// Tries to get a metadata value.
        /// </summary>
        /// <param name="entry">The ledger entry.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        /// <returns><c>true</c> when the metadata value exists; otherwise, <c>false</c>.</returns>
        private static bool TryGetMetadataValue(
            AiDecisionLedgerEntry entry,
            string key,
            out string? value)
        {
            value = null;

            if (entry.Metadata is null)
            {
                return false;
            }

            return entry.Metadata.TryGetValue(key, out value);
        }


        

        /// <summary>
        /// Parses a ledger category returned by MCP.
        /// </summary>
        /// <param name="category">The category value.</param>
        /// <returns>The parsed ledger category.</returns>
        private static AiDecisionLedgerCategory ParseLedgerCategory(
            string? category)
        {
            return Enum.TryParse<AiDecisionLedgerCategory>(
                category,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : AiDecisionLedgerCategory.Control;
        }

        /// <summary>
        /// Parses a ledger outcome returned by MCP.
        /// </summary>
        /// <param name="outcome">The outcome value.</param>
        /// <returns>The parsed ledger outcome.</returns>
        private static AiDecisionLedgerOutcome ParseLedgerOutcome(
            string? outcome)
        {
            return Enum.TryParse<AiDecisionLedgerOutcome>(
                outcome,
                ignoreCase: true,
                out var parsed)
                ? parsed
                : AiDecisionLedgerOutcome.CompletedWithIssues;
        }



        /// <summary>
        /// Verifies that concurrent failures in two tenants do not leak recovery, forensics,
        /// incidents, or redispatch into a third tenant that remains healthy.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Two_Failed_Tenants_While_Leaving_Third_Tenant_Untouched()
        {
            var scenario =
                CreateConcurrentSafeTenantRecoveryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(2);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(5);

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

            var registry =
                host.Services.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var healthReconciler =
                host.Services.GetRequiredService<IAiRuntimeInstanceHealthReconciler>();

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var sharedRunStore =
                host.Services.GetRequiredService<IAiSharedRunStore>();

            var sharedQueue =
                host.Services.GetRequiredService<IAiSharedQueue>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var scaleOutPublisher =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestPublisher>();

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var sharedQueueDispatcher =
                host.Services.GetRequiredService<IAiSharedQueueDispatcher>();

            var queryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenantA =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-a", StringComparison.Ordinal));

            var tenantB =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-b", StringComparison.Ordinal));

            var tenantC =
                scenario.Tenants.Single(tenant =>
                    string.Equals(tenant.TenantId, "tenant-concurrent-c", StringComparison.Ordinal));

            using var tenantAHttpClient =
                host.CreateClient();

            using var tenantBHttpClient =
                host.CreateClient();

            using var tenantCHttpClient =
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

            var tenantCMcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantCHttpClient,
                        RequestedBy,
                        tenantId: tenantC.TenantId,
                        tenantGroupId: tenantC.TenantGroupId)
                    .ConfigureAwait(false);

            var tenantAPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            var tenantAControlPipelineName =
                $"{scenario.Name}-{tenantA.TenantId}-control-runtime-{Guid.NewGuid():N}";

            var tenantBPipelineName =
                $"{scenario.Name}-{tenantB.TenantId}-concurrent-recovery-{Guid.NewGuid():N}";

            var tenantCSafePipelineName =
                $"{scenario.Name}-{tenantC.TenantId}-safe-runtime-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[MULTI-TENANT SAFE RECOVERY] Starting. ControlPlaneId='{controlPlaneId}', TenantAPipeline='{tenantAPipelineName}', TenantAControlPipeline='{tenantAControlPipelineName}', TenantBPipeline='{tenantBPipelineName}', TenantCSafePipeline='{tenantCSafePipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var tenantAFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantAMcp,
                        scaleOutRequestStore,
                        tenantA,
                        controlPlaneId,
                        tenantAPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantAControlRuntimeInstanceId =
                await PublishScaleOutAndWaitForAdditionalTenantRuntimeInstanceAsync(
                        scaleOutPublisher,
                        registry,
                        tenantAFailedBootstrap,
                        tenantA,
                        controlPlaneId,
                        tenantAControlPipelineName,
                        tenantAFailedBootstrap.AssignedRuntimeInstanceId!,
                        scenario.ScaleOutTimeout)
                    .ConfigureAwait(false);

            var tenantBFailedBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantBMcp,
                        scaleOutRequestStore,
                        tenantB,
                        controlPlaneId,
                        tenantBPipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantCSafeBootstrap =
                await SubmitAndDispatchOneRunAsync(
                        tenantCMcp,
                        scaleOutRequestStore,
                        tenantC,
                        controlPlaneId,
                        tenantCSafePipelineName,
                        scenario.ScaleOutTimeout,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantAFailedBootstrap.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantBFailedBootstrap.LocalRunId));
            Assert.False(string.IsNullOrWhiteSpace(tenantCSafeBootstrap.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(tenantCSafeBootstrap.LocalRunId));

            var tenantAFailedRuntimeInstanceId =
                tenantAFailedBootstrap.AssignedRuntimeInstanceId!;

            var tenantBFailedRuntimeInstanceId =
                tenantBFailedBootstrap.AssignedRuntimeInstanceId!;

            var tenantCSafeRuntimeInstanceId =
                tenantCSafeBootstrap.AssignedRuntimeInstanceId!;

            this.output.WriteLine(
                "[MULTI-TENANT SAFE RUNTIME SELECTION] " +
                $"TenantAFailedRuntime='{tenantAFailedRuntimeInstanceId}', " +
                $"TenantAControlRuntime='{tenantAControlRuntimeInstanceId}', " +
                $"TenantBFailedRuntime='{tenantBFailedRuntimeInstanceId}', " +
                $"TenantCSafeRuntime='{tenantCSafeRuntimeInstanceId}'.");

            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantAControlRuntimeInstanceId);
            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantBFailedRuntimeInstanceId);
            Assert.NotEqual(tenantAFailedRuntimeInstanceId, tenantCSafeRuntimeInstanceId);
            Assert.NotEqual(tenantAControlRuntimeInstanceId, tenantBFailedRuntimeInstanceId);
            Assert.NotEqual(tenantAControlRuntimeInstanceId, tenantCSafeRuntimeInstanceId);
            Assert.NotEqual(tenantBFailedRuntimeInstanceId, tenantCSafeRuntimeInstanceId);

            AssertRuntimeBelongsToTenant(tenantAFailedRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantAControlRuntimeInstanceId, tenantA);
            AssertRuntimeBelongsToTenant(tenantBFailedRuntimeInstanceId, tenantB);
            AssertRuntimeBelongsToTenant(tenantCSafeRuntimeInstanceId, tenantC);

            await AssertSafeTenantUntouchedAsync(
                    registry,
                    queryService,
                    tenantC,
                    tenantCSafeRuntimeInstanceId,
                    allForensics: Array.Empty<AiRuntimeRecoveryForensicsReadModel>())
                .ConfigureAwait(false);

            var tenantASeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantAFailedBootstrap,
                        tenantA,
                        tenantAPipelineName,
                        tenantAFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantAFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantBSeededWorks =
                await ProductionRecoverySeedHelpers
                    .SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        tenantBFailedBootstrap,
                        tenantB,
                        tenantBPipelineName,
                        tenantBFailedRuntimeInstanceId,
                        queuedLocalRunCount: 1,
                        inFlightExecutionCount: TenantBFailedWorkCount - 1,
                        stepCount: StepCount,
                        failureStepNumber: FailureStepNumber,
                        requestedBy: RequestedBy,
                        source: Source)
                    .ConfigureAwait(false);

            var tenantAGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantA,
                    FailedRuntimeInstanceId = tenantAFailedRuntimeInstanceId,
                    SeededWorks = tenantASeededWorks
                };

            var tenantBGroup =
                new FailedRuntimeRecoveryGroup
                {
                    Tenant = tenantB,
                    FailedRuntimeInstanceId = tenantBFailedRuntimeInstanceId,
                    SeededWorks = tenantBSeededWorks
                };

            var failedRuntimeGroups =
                new[]
                {
                    tenantAGroup,
                    tenantBGroup
                };

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks);

            WriteFailedRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks);

            await WaitForSeededRuntimeGroupsVisibleAsync(
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            await MarkUnhealthyAndReconcileUntilAllRuntimeGroupsRecoveredAsync(
                    registry,
                    healthReconciler,
                    recoveryReconciler,
                    runExecutionIndex,
                    failedRuntimeGroups,
                    TimeSpan.FromSeconds(120))
                .ConfigureAwait(false);

            await AssertControlRuntimeUntouchedBeforeRedispatchAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId)
                .ConfigureAwait(false);

            await AssertSafeTenantUntouchedAsync(
                    registry,
                    queryService,
                    tenantC,
                    tenantCSafeRuntimeInstanceId,
                    allForensics: Array.Empty<AiRuntimeRecoveryForensicsReadModel>())
                .ConfigureAwait(false);

            var tenantARedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantAGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var tenantBRedispatchedRuns =
                await WaitForRedispatchedRunsAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedQueue,
                        sharedQueueDispatcher,
                        tenantBGroup,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            var allRedispatchedRuns =
                tenantARedispatchedRuns
                    .Concat(tenantBRedispatchedRuns)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allRedispatchedRuns.Length);

            AssertNoSelfRedispatch(
                failedRuntimeGroups,
                allRedispatchedRuns);

            AssertNoCrossTenantRedispatch(
                tenantAGroup,
                tenantARedispatchedRuns,
                tenantBGroup,
                tenantBRedispatchedRuns);

            Assert.DoesNotContain(
                allRedispatchedRuns,
                run =>
                    string.Equals(run.AssignedRuntimeInstanceId, tenantCSafeRuntimeInstanceId, StringComparison.Ordinal) ||
                    string.Equals(run.SharedRunId, tenantCSafeBootstrap.SharedRunId, StringComparison.Ordinal));

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantASeededWorks,
                tenantARedispatchedRuns);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBSeededWorks,
                tenantBRedispatchedRuns);

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

            AssertAllRecoveredRunsCompleted(tenantAFinalStatuses);
            AssertAllRecoveredRunsCompleted(tenantBFinalStatuses);

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantAGroup,
                    tenantARedispatchedRuns)
                .ConfigureAwait(false);

            await AssertRecoveredExecutionIndexesCompletedAsync(
                    runExecutionIndex,
                    tenantBGroup,
                    tenantBRedispatchedRuns)
                .ConfigureAwait(false);

            var tenantAForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantAGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var tenantBForensics =
                await WaitForRecoveredForensicsAsync(
                        queryService,
                        tenantBGroup,
                        TimeSpan.FromSeconds(45))
                    .ConfigureAwait(false);

            var allForensics =
                tenantAForensics
                    .Concat(tenantBForensics)
                    .ToArray();

            Assert.Equal(CountSeededWork(failedRuntimeGroups), allForensics.Length);

            AssertNoDuplicateForensics(allForensics);
            AssertNoCrossTenantForensicsLeak(tenantAGroup, tenantAForensics, tenantBGroup, tenantBForensics);
            AssertNoCrossIncidentForensicsLeak(tenantAForensics, tenantBForensics);

            await AssertControlRuntimeUntouchedAfterRecoveryAsync(
                    registry,
                    queryService,
                    tenantA,
                    tenantAControlRuntimeInstanceId,
                    allForensics)
                .ConfigureAwait(false);

            await AssertSafeTenantUntouchedAsync(
                    registry,
                    queryService,
                    tenantC,
                    tenantCSafeRuntimeInstanceId,
                    allForensics)
                .ConfigureAwait(false);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantAFailedRuntimeInstanceId,
                tenantAForensics);

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                tenantBFailedRuntimeInstanceId,
                tenantBForensics);

            this.output.WriteLine(
                "[MULTI-TENANT SAFE RECOVERY PROOF] " +
                $"RuntimeA='{tenantAFailedRuntimeInstanceId}' -> '{tenantARedispatchedRuns.Count}/{tenantASeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantARedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"RuntimeB='{tenantBFailedRuntimeInstanceId}' -> '{tenantBRedispatchedRuns.Count}/{tenantBSeededWorks.Count}' recovered -> ReplacementRuntimeInstances='{string.Join(",", tenantBRedispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}', " +
                $"SafeTenant='{tenantC.TenantId}', SafeRuntime='{tenantCSafeRuntimeInstanceId}' -> untouched, 0 forensics events, " +
                $"ExpectedForensics='{CountSeededWork(failedRuntimeGroups)}', ActualForensics='{allForensics.Length}', " +
                $"CrossTenantLeakDetected='false', CrossIncidentLeakDetected='false', DuplicateRecoveryDetected='false', SelfRedispatchDetected='false'.");
        }

        private sealed record FailedRuntimeRecoveryGroup
        {
            public required ProductionTenantScenarioDefinition Tenant { get; init; }

            public required string FailedRuntimeInstanceId { get; init; }

            public required IReadOnlyList<FailedRuntimeWorkSeed> SeededWorks { get; init; }
        }

        private static async Task<AiSharedRunRecord> SubmitAndDispatchOneRunAsync(
            McpTestClient mcp,
            IAiRuntimeScaleOutRequestStore scaleOutRequestStore,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            TimeSpan scaleOutTimeout,
            TimeSpan dispatchTimeout)
        {
            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
                        controlPlaneId,
                        pipelineName,
                        RequestedBy,
                        Source)
                    .ConfigureAwait(false);

            await ProductionSharedRunTestHelpers
                .WaitForAnyTenantScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    controlPlaneId,
                    tenant,
                    pipelineName,
                    scaleOutTimeout)
                .ConfigureAwait(false);

            return await ProductionSharedRunTestHelpers
                .WaitForSingleDispatchedRunAsync(
                    mcp,
                    pipelineName,
                    sharedRunId,
                    dispatchTimeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes an explicit tenant scale-out request and waits for an additional dedicated runtime instance.
        /// </summary>
        /// <param name="scaleOutPublisher">The scale-out request publisher.</param>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="templateSharedRun">A tenant-scoped shared run used as a request template.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="pipelineName">The pipeline key for the control runtime prewarm request.</param>
        /// <param name="excludedRuntimeInstanceId">The runtime instance identifier to exclude.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The additional tenant runtime instance identifier.</returns>
        private static async Task<string> PublishScaleOutAndWaitForAdditionalTenantRuntimeInstanceAsync(
            IAiRuntimeScaleOutRequestPublisher scaleOutPublisher,
            IAiRuntimeInstanceRegistry registry,
            AiSharedRunRecord templateSharedRun,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string excludedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(scaleOutPublisher);
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(excludedRuntimeInstanceId);

            var scaleOutSharedRun =
                CreateScaleOutTemplateSharedRun(
                    templateSharedRun,
                    controlPlaneId,
                    pipelineName);

            var isolation =
                ResolveIsolationSettings(tenant);

            var snapshots =
                await registry
                    .ListAsync()
                    .ConfigureAwait(false);

            var currentTenantInstanceCount =
                snapshots.Count(snapshot =>
                    string.Equals(snapshot.ControlPlaneId, controlPlaneId, StringComparison.Ordinal) &&
                    RuntimeSnapshotBelongsToTenant(snapshot.RuntimeInstanceId, tenant));

            var metadata =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleout.intent"] = "concurrent-recovery-control-runtime-prewarm",
                    ["scaleout.requestId"] = $"scale-out-control-runtime-{scaleOutSharedRun.SharedRunId}-{Guid.NewGuid():N}",
                    ["controlPlaneId"] = controlPlaneId,
                    ["tenantId"] = tenant.TenantId,
                    ["tenantGroupId"] = tenant.TenantGroupId ?? string.Empty,
                    ["pipelineKey"] = pipelineName,
                    ["purpose"] = "concurrent-recovery-control-runtime-prewarm",
                    ["excludedRuntimeInstanceId"] = excludedRuntimeInstanceId
                };

            var publishResult =
                await scaleOutPublisher
                    .PublishAsync(
                        new AiRuntimeScaleOutRequest
                        {
                            SharedRun = scaleOutSharedRun,
                            SharedRunId = scaleOutSharedRun.SharedRunId,
                            ExecutionContextSnapshot = scaleOutSharedRun.ExecutionContextSnapshot,
                            TenantId = tenant.TenantId,
                            TenantGroupId = tenant.TenantGroupId,
                            PipelineKey = pipelineName,
                            IsolationMode = isolation.IsolationMode,
                            PreferDedicatedCapacity = isolation.PreferDedicatedCapacity,
                            AllowSharedFallback = isolation.AllowSharedFallback,
                            MaxRuntimeInstances = tenant.MaxRuntimeInstances,
                            RuntimeInstanceIdPrefix = tenant.RuntimeInstanceIdPrefix,
                            WorkerCountPerInstance = tenant.WorkerCountPerInstance,
                            MaxConcurrentRunsPerInstance = tenant.MaxConcurrentRunsPerInstance,
                            LocalQueueCapacity = tenant.LocalQueueCapacity,
                            VisibleInstanceCount = currentTenantInstanceCount,
                            AvailableInstanceCount = 0,
                            CurrentInstanceCount = currentTenantInstanceCount,
                            MaxInstanceCount = tenant.MaxRuntimeInstances,
                            CorrelationId = scaleOutSharedRun.CorrelationId,
                            RequestedBy = RequestedBy,
                            Source = Source,
                            Reason = "Prewarming an additional dedicated tenant runtime for concurrent recovery control.",
                            Metadata = metadata
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);

            Assert.True(
                publishResult.Success,
                $"Control runtime scale-out request failed. TenantId='{tenant.TenantId}', ScaleOutRequestId='{publishResult.ScaleOutRequestId}', Message='{publishResult.Message}', FailureReason='{publishResult.FailureReason}'.");

            return await WaitForAdditionalTenantRuntimeInstanceAsync(
                    registry,
                    tenant,
                    controlPlaneId,
                    excludedRuntimeInstanceId,
                    timeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Maps a production tenant runtime mode to runtime instance isolation settings.
        /// </summary>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <returns>The resolved runtime isolation settings.</returns>
        private static (AiRuntimeInstanceIsolationMode IsolationMode, bool PreferDedicatedCapacity, bool AllowSharedFallback) ResolveIsolationSettings(
            ProductionTenantScenarioDefinition tenant)
        {
            ArgumentNullException.ThrowIfNull(tenant);

            return tenant.RuntimeMode switch
            {
                ProductionTenantRuntimeMode.Dedicated => (AiRuntimeInstanceIsolationMode.Dedicated, true, false),
                ProductionTenantRuntimeMode.Shared => (AiRuntimeInstanceIsolationMode.Shared, false, true),
                ProductionTenantRuntimeMode.Hybrid => (AiRuntimeInstanceIsolationMode.Hybrid, true, true),
                _ => (AiRuntimeInstanceIsolationMode.Dedicated, true, false)
            };
        }

        /// <summary>
        /// Creates a scale-out-only shared run template from an existing tenant-scoped shared run.
        /// </summary>
        /// <param name="templateSharedRun">The source shared run.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="pipelineName">The pipeline key.</param>
        /// <returns>The scale-out template shared run.</returns>
        private static AiSharedRunRecord CreateScaleOutTemplateSharedRun(
            AiSharedRunRecord templateSharedRun,
            string controlPlaneId,
            string pipelineName)
        {
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var sharedRunId =
                $"control-runtime-scaleout-{Guid.NewGuid():N}";

            var metadata =
                new Dictionary<string, string>(
                    templateSharedRun.Metadata,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["controlPlaneId"] = controlPlaneId,
                    ["pipelineKey"] = pipelineName,
                    ["purpose"] = "concurrent-recovery-control-runtime-prewarm"
                };

            return new AiSharedRunRecord
            {
                SharedRunId = sharedRunId,
                ControlPlaneId = controlPlaneId,
                Status = templateSharedRun.Status,
                RunRequest = templateSharedRun.RunRequest,
                ExecutionContextSnapshot = templateSharedRun.ExecutionContextSnapshot,
                LocalRunId = null,
                ExecutionId = null,
                AssignedRuntimeInstanceId = null,
                AdmissionDecision = templateSharedRun.AdmissionDecision,
                PipelineKey = pipelineName,
                CorrelationId = $"control-runtime-scaleout-{Guid.NewGuid():N}",
                RequestedBy = RequestedBy,
                Source = Source,
                Reason = "concurrent-recovery-control-runtime-prewarm",
                FailureReason = null,
                SubmittedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = metadata
            };
        }

        /// <summary>
        /// Waits for an additional alive dedicated runtime instance for the tenant, different from the excluded runtime.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="controlPlaneId">The control-plane identifier.</param>
        /// <param name="excludedRuntimeInstanceId">The runtime instance identifier to exclude.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The additional tenant runtime instance identifier.</returns>
        private static async Task<string> WaitForAdditionalTenantRuntimeInstanceAsync(
            IAiRuntimeInstanceRegistry registry,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string excludedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(excludedRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            var lastKnownRuntimeInstances =
                string.Empty;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await registry
                        .ListAsync()
                        .ConfigureAwait(false);

                lastKnownRuntimeInstances =
                    string.Join(
                        ",",
                        snapshots.Select(snapshot => $"{snapshot.RuntimeInstanceId}:{snapshot.Status}"));

                var candidate =
                    snapshots
                        .Where(snapshot =>
                            string.Equals(snapshot.ControlPlaneId, controlPlaneId, StringComparison.Ordinal) &&
                            !string.Equals(snapshot.RuntimeInstanceId, excludedRuntimeInstanceId, StringComparison.Ordinal) &&
                            RuntimeSnapshotBelongsToTenant(snapshot.RuntimeInstanceId, tenant) &&
                            !string.Equals(snapshot.Status.ToString(), "Unhealthy", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(snapshot.Status.ToString(), "Draining", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                        .FirstOrDefault();

                if (candidate is not null)
                {
                    return candidate.RuntimeInstanceId;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Could not find an additional alive dedicated runtime instance for tenant after explicit scale-out request. " +
                $"TenantId='{tenant.TenantId}', RuntimeInstanceIdPrefix='{tenant.RuntimeInstanceIdPrefix}', ExcludedRuntimeInstanceId='{excludedRuntimeInstanceId}', " +
                $"KnownRuntimeInstances='{lastKnownRuntimeInstances}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Determines whether a runtime instance id belongs to the expected tenant.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <returns>True when the runtime belongs to the tenant.</returns>
        private static bool RuntimeSnapshotBelongsToTenant(
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            return runtimeInstanceId.Contains(tenant.TenantId, StringComparison.Ordinal) ||
                runtimeInstanceId.Contains(tenant.RuntimeInstanceIdPrefix, StringComparison.Ordinal);
        }

        private static async Task WaitForSeededRuntimeGroupsVisibleAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IReadOnlyCollection<FailedRuntimeRecoveryGroup> groups,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            var lastDiagnostics =
                string.Empty;

            while (DateTimeOffset.UtcNow < deadline)
            {
                var missing =
                    new List<string>();

                foreach (var group in groups)
                {
                    foreach (var work in group.SeededWorks)
                    {
                        var entry =
                            await runExecutionIndex
                                .GetAsync(work.FailedLocalRunId)
                                .ConfigureAwait(false);

                        var runtimeMatches =
                            string.Equals(entry?.RuntimeInstanceId, group.FailedRuntimeInstanceId, StringComparison.Ordinal);

                        var executionMatches =
                            string.IsNullOrWhiteSpace(work.ExecutionId) ||
                            string.Equals(entry?.ExecutionId, work.ExecutionId, StringComparison.Ordinal);

                        var statusIsRecoverable =
                            string.Equals(entry?.Status, "queued", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry?.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry?.Status, "started", StringComparison.OrdinalIgnoreCase);

                        if (entry is not null &&
                            runtimeMatches &&
                            executionMatches &&
                            statusIsRecoverable)
                        {
                            continue;
                        }

                        missing.Add(
                            $"Tenant='{group.Tenant.TenantId}', FailedRuntime='{group.FailedRuntimeInstanceId}', SharedRun='{work.SharedRunId}', LocalRun='{work.FailedLocalRunId}', ExpectedExecution='{work.ExecutionId}', ActualExecution='{entry?.ExecutionId}', ActualRuntime='{entry?.RuntimeInstanceId}', ActualStatus='{entry?.Status}'");
                    }
                }

                if (missing.Count == 0)
                {
                    return;
                }

                lastDiagnostics =
                    string.Join(" | ", missing);

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Seeded concurrent failed-runtime inventory was not fully visible before recovery. " +
                $"Diagnostics='{lastDiagnostics}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        private static async Task MarkUnhealthyAndReconcileUntilAllRuntimeGroupsRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IReadOnlyCollection<FailedRuntimeRecoveryGroup> groups,
            TimeSpan timeout)
        {
            var failedRuntimeIds =
                groups
                    .Select(group => group.FailedRuntimeInstanceId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeExecutionRecoveryReconciliationResult? lastResult = null;
            var lastStatuses =
                new Dictionary<string, string?>(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.WhenAll(
                        failedRuntimeIds.Select(runtimeInstanceId =>
                            registry.MarkUnhealthyAsync(runtimeInstanceId)))
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                await Task.WhenAll(
                        failedRuntimeIds.Select(runtimeInstanceId =>
                            registry.MarkUnhealthyAsync(runtimeInstanceId)))
                    .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                lastStatuses.Clear();

                foreach (var work in groups.SelectMany(group => group.SeededWorks))
                {
                    var entry =
                        await runExecutionIndex
                            .GetAsync(work.FailedLocalRunId)
                            .ConfigureAwait(false);

                    lastStatuses[work.FailedLocalRunId] =
                        entry?.Status;
                }

                if (groups
                    .SelectMany(group => group.SeededWorks)
                    .All(work =>
                        string.Equals(
                            lastStatuses[work.FailedLocalRunId],
                            "requeued-for-recovery",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Concurrent runtime execution recovery did not recover all seeded failed-runtime work within the timeout. " +
                $"FailedRuntimeInstances='{string.Join(",", failedRuntimeIds)}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}', " +
                $"LastDiscoveredUnfinishedRunCount='{lastResult?.DiscoveredUnfinishedRunCount}', " +
                $"LastStatuses='{string.Join(",", lastStatuses.Select(pair => $"{pair.Key}:{pair.Value}"))}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        private static async Task<IReadOnlyList<AiSharedRunRecord>> WaitForRedispatchedRunsAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiSharedQueueDispatcher sharedQueueDispatcher,
            FailedRuntimeRecoveryGroup group,
            TimeSpan timeout)
        {
            var redispatchedRuns =
                new List<AiSharedRunRecord>();

            foreach (var work in group.SeededWorks)
            {
                var redispatchedRun =
                    await ProductionRecoveryWaitHelpers
                        .WaitForSharedRunAssignedAwayFromRuntimeAsync(
                            registry,
                            healthReconciler,
                            sharedRunStore,
                            sharedQueue,
                            sharedQueueDispatcher,
                            work.SharedRunId,
                            group.FailedRuntimeInstanceId,
                            timeout)
                        .ConfigureAwait(false);

                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
                Assert.NotEqual(group.FailedRuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
                Assert.NotEqual(work.FailedLocalRunId, redispatchedRun.LocalRunId);
                AssertRuntimeBelongsToTenant(redispatchedRun.AssignedRuntimeInstanceId!, group.Tenant);

                if (!string.IsNullOrWhiteSpace(work.ExecutionId))
                {
                    Assert.Equal(work.ExecutionId, redispatchedRun.ExecutionId);
                }

                redispatchedRuns.Add(redispatchedRun);
            }

            return redispatchedRuns;
        }

        private static async Task<IReadOnlyList<AiRuntimeRecoveryForensicsReadModel>> WaitForRecoveredForensicsAsync(
            IAiRuntimeRecoveryForensicsQueryService queryService,
            FailedRuntimeRecoveryGroup group,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> matchingRecords =
                Array.Empty<AiRuntimeRecoveryForensicsReadModel>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var result =
                    await queryService
                        .SearchAsync(
                            new AiRuntimeRecoveryForensicsQuery
                            {
                                RuntimeInstanceId = group.FailedRuntimeInstanceId,
                                TenantId = group.Tenant.TenantId,
                                Limit = 200
                            })
                        .ConfigureAwait(false);

                matchingRecords =
                    result.Items
                        .Where(item => group.SeededWorks.Any(work =>
                            string.Equals(work.SharedRunId, item.SharedRunId, StringComparison.Ordinal)))
                        .ToArray();

                if (matchingRecords.Count == group.SeededWorks.Count)
                {
                    return matchingRecords;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                "Recovered forensics did not match seeded failed-runtime work inventory. " +
                $"TenantId='{group.Tenant.TenantId}', FailedRuntimeInstanceId='{group.FailedRuntimeInstanceId}', " +
                $"ExpectedForensics='{group.SeededWorks.Count}', ActualForensics='{matchingRecords.Count}', " +
                $"ActualIds='{string.Join(",", matchingRecords.Select(record => record.ForensicsId))}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        private static async Task AssertRecoveredExecutionIndexesCompletedAsync(
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            FailedRuntimeRecoveryGroup group,
            IReadOnlyCollection<AiSharedRunRecord> redispatchedRuns)
        {
            foreach (var work in group.SeededWorks.Where(work => !string.IsNullOrWhiteSpace(work.ExecutionId)))
            {
                var recoveredRun =
                    redispatchedRuns.Single(run =>
                        string.Equals(run.SharedRunId, work.SharedRunId, StringComparison.Ordinal));

                var replacementIndex =
                    await ProductionRecoveryWaitHelpers
                        .WaitForRunExecutionIndexAsync(
                            runExecutionIndex,
                            recoveredRun.LocalRunId!,
                            work.ExecutionId!,
                            TimeSpan.FromSeconds(20))
                        .ConfigureAwait(false);

                Assert.Equal(work.ExecutionId, replacementIndex.ExecutionId);
                Assert.Equal(recoveredRun.AssignedRuntimeInstanceId, replacementIndex.RuntimeInstanceId);
                Assert.Equal("completed", replacementIndex.Status);
            }
        }

        /// <summary>
        /// Asserts that the control runtime remains alive and has no recovery forensics.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="queryService">The recovery forensics query service.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="controlRuntimeInstanceId">The control runtime instance identifier.</param>
        private static async Task AssertControlRuntimeUntouchedBeforeRedispatchAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRecoveryForensicsQueryService queryService,
            ProductionTenantScenarioDefinition tenant,
            string controlRuntimeInstanceId)
        {
            var controlSnapshot =
                await registry
                    .GetAsync(controlRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(controlSnapshot);

            Assert.False(
                string.Equals(controlSnapshot!.Status.ToString(), "Unhealthy", StringComparison.OrdinalIgnoreCase),
                $"Control runtime should not be unhealthy. RuntimeInstanceId='{controlRuntimeInstanceId}', Status='{controlSnapshot.Status}'.");

            Assert.False(
                string.Equals(controlSnapshot.Status.ToString(), "Draining", StringComparison.OrdinalIgnoreCase),
                $"Control runtime should not be draining. RuntimeInstanceId='{controlRuntimeInstanceId}', Status='{controlSnapshot.Status}'.");

            var controlForensics =
                await queryService
                    .SearchAsync(
                        new AiRuntimeRecoveryForensicsQuery
                        {
                            RuntimeInstanceId = controlRuntimeInstanceId,
                            TenantId = tenant.TenantId,
                            Limit = 20
                        })
                    .ConfigureAwait(false);

            Assert.Empty(controlForensics.Items);
        }

        /// <summary>
        /// Asserts that the control runtime remains untouched after recovery.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="queryService">The recovery forensics query service.</param>
        /// <param name="tenant">The tenant definition.</param>
        /// <param name="controlRuntimeInstanceId">The control runtime instance identifier.</param>
        /// <param name="allForensics">All recovered forensics records.</param>
        private static async Task AssertControlRuntimeUntouchedAfterRecoveryAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRecoveryForensicsQueryService queryService,
            ProductionTenantScenarioDefinition tenant,
            string controlRuntimeInstanceId,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> allForensics)
        {
            await AssertControlRuntimeUntouchedBeforeRedispatchAsync(
                    registry,
                    queryService,
                    tenant,
                    controlRuntimeInstanceId)
                .ConfigureAwait(false);

            Assert.DoesNotContain(
                allForensics,
                record =>
                    string.Equals(
                        record.Record.Failure?.FailedRuntimeInstanceId,
                        controlRuntimeInstanceId,
                        StringComparison.Ordinal) ||
                    record.ForensicsId.Contains(controlRuntimeInstanceId, StringComparison.Ordinal));
        }


        /// <summary>
        /// Asserts that a safe tenant runtime remains healthy and has no recovery forensics.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="queryService">The recovery forensics query service.</param>
        /// <param name="tenant">The safe tenant definition.</param>
        /// <param name="safeRuntimeInstanceId">The safe runtime instance identifier.</param>
        /// <param name="allForensics">The recovered forensics records already collected by the test.</param>
        private static async Task AssertSafeTenantUntouchedAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeRecoveryForensicsQueryService queryService,
            ProductionTenantScenarioDefinition tenant,
            string safeRuntimeInstanceId,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> allForensics)
        {
            var safeSnapshot =
                await registry
                    .GetAsync(safeRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(safeSnapshot);

            Assert.False(
                string.Equals(safeSnapshot!.Status.ToString(), "Unhealthy", StringComparison.OrdinalIgnoreCase),
                $"Safe tenant runtime should not be unhealthy. TenantId='{tenant.TenantId}', RuntimeInstanceId='{safeRuntimeInstanceId}', Status='{safeSnapshot.Status}'.");

            Assert.False(
                string.Equals(safeSnapshot.Status.ToString(), "Draining", StringComparison.OrdinalIgnoreCase),
                $"Safe tenant runtime should not be draining. TenantId='{tenant.TenantId}', RuntimeInstanceId='{safeRuntimeInstanceId}', Status='{safeSnapshot.Status}'.");

            var tenantForensics =
                await queryService
                    .SearchAsync(
                        new AiRuntimeRecoveryForensicsQuery
                        {
                            TenantId = tenant.TenantId,
                            Limit = 200
                        })
                    .ConfigureAwait(false);

            Assert.Empty(tenantForensics.Items);

            var runtimeForensics =
                await queryService
                    .SearchAsync(
                        new AiRuntimeRecoveryForensicsQuery
                        {
                            RuntimeInstanceId = safeRuntimeInstanceId,
                            TenantId = tenant.TenantId,
                            Limit = 200
                        })
                    .ConfigureAwait(false);

            Assert.Empty(runtimeForensics.Items);

            Assert.DoesNotContain(
                allForensics,
                record =>
                    string.Equals(record.TenantId, tenant.TenantId, StringComparison.Ordinal) ||
                    string.Equals(record.Record.Failure?.FailedRuntimeInstanceId, safeRuntimeInstanceId, StringComparison.Ordinal) ||
                    string.Equals(record.Record.Replacement?.ReplacementRuntimeInstanceId, safeRuntimeInstanceId, StringComparison.Ordinal) ||
                    record.ForensicsId.Contains(safeRuntimeInstanceId, StringComparison.Ordinal));
        }

        private static void AssertAllRecoveredRunsCompleted(
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

        private static void AssertNoSelfRedispatch(
            IReadOnlyCollection<FailedRuntimeRecoveryGroup> groups,
            IReadOnlyCollection<AiSharedRunRecord> redispatchedRuns)
        {
            foreach (var group in groups)
            {
                Assert.DoesNotContain(
                    redispatchedRuns,
                    run =>
                        group.SeededWorks.Any(work =>
                            string.Equals(work.SharedRunId, run.SharedRunId, StringComparison.Ordinal)) &&
                        string.Equals(run.AssignedRuntimeInstanceId, group.FailedRuntimeInstanceId, StringComparison.Ordinal));
            }
        }

        private static void AssertNoCrossTenantRedispatch(
            FailedRuntimeRecoveryGroup tenantAGroup,
            IReadOnlyCollection<AiSharedRunRecord> tenantARedispatchedRuns,
            FailedRuntimeRecoveryGroup tenantBGroup,
            IReadOnlyCollection<AiSharedRunRecord> tenantBRedispatchedRuns)
        {
            foreach (var run in tenantARedispatchedRuns)
            {
                AssertRuntimeBelongsToTenant(run.AssignedRuntimeInstanceId!, tenantAGroup.Tenant);
                Assert.DoesNotContain(
                    tenantBGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, run.SharedRunId, StringComparison.Ordinal));
            }

            foreach (var run in tenantBRedispatchedRuns)
            {
                AssertRuntimeBelongsToTenant(run.AssignedRuntimeInstanceId!, tenantBGroup.Tenant);
                Assert.DoesNotContain(
                    tenantAGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, run.SharedRunId, StringComparison.Ordinal));
            }
        }

        private static void AssertNoCrossTenantForensicsLeak(
            FailedRuntimeRecoveryGroup tenantAGroup,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> tenantAForensics,
            FailedRuntimeRecoveryGroup tenantBGroup,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> tenantBForensics)
        {
            foreach (var record in tenantAForensics)
            {
                Assert.Equal(tenantAGroup.Tenant.TenantId, record.TenantId);
                Assert.Contains(
                    tenantAGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, record.SharedRunId, StringComparison.Ordinal));
                Assert.DoesNotContain(
                    tenantBGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, record.SharedRunId, StringComparison.Ordinal));
            }

            foreach (var record in tenantBForensics)
            {
                Assert.Equal(tenantBGroup.Tenant.TenantId, record.TenantId);
                Assert.Contains(
                    tenantBGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, record.SharedRunId, StringComparison.Ordinal));
                Assert.DoesNotContain(
                    tenantAGroup.SeededWorks,
                    work => string.Equals(work.SharedRunId, record.SharedRunId, StringComparison.Ordinal));
            }
        }

        private static void AssertNoDuplicateForensics(
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> records)
        {
            var duplicateGroups =
                records
                    .GroupBy(record => record.ForensicsId, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .ToArray();

            Assert.True(
                duplicateGroups.Length == 0,
                "Duplicate recovery forensics records were detected. " +
                $"Duplicates='{string.Join(",", duplicateGroups.Select(group => $"{group.Key}:{group.Count()}"))}'.");
        }

        private static void AssertNoCrossIncidentForensicsLeak(
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> runtimeAForensics,
            IReadOnlyCollection<AiRuntimeRecoveryForensicsReadModel> runtimeBForensics)
        {
            var runtimeAIncidentIds =
                runtimeAForensics
                    .Select(TryResolveRuntimeFailureIncidentId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var runtimeBIncidentIds =
                runtimeBForensics
                    .Select(TryResolveRuntimeFailureIncidentId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                runtimeAIncidentIds.Length > 0,
                "Runtime A recovery forensics must expose a RuntimeFailureIncidentId.");

            Assert.True(
                runtimeBIncidentIds.Length > 0,
                "Runtime B recovery forensics must expose a RuntimeFailureIncidentId.");

            Assert.Empty(
                runtimeAIncidentIds.Intersect(runtimeBIncidentIds, StringComparer.Ordinal));
        }

        /// <summary>
        /// Writes the failed runtime inventory before recovery.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="seededWorks">The seeded work inventory.</param>
        private static void WriteFailedRuntimeWorkInventory(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(seededWorks);

            output.WriteLine("[FAILED RUNTIME WORK INVENTORY]");
            output.WriteLine($"RuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"LocalQueuedRunCount='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.LocalQueued)}'");
            output.WriteLine($"InFlightExecutionCount='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.InFlightExecution)}'");
            output.WriteLine($"TotalRecoverableWorkCount='{seededWorks.Count}'");

            var index =
                1;

            foreach (var work in seededWorks)
            {
                output.WriteLine(
                    $"{index:00}. Kind='{work.Kind}', SharedRunId='{work.SharedRunId}', FailedLocalRunId='{work.FailedLocalRunId}', ExecutionId='{work.ExecutionId}'.");

                index++;
            }
        }

        /// <summary>
        /// Writes the recovered runtime inventory after redispatch.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="seededWorks">The seeded work inventory.</param>
        /// <param name="redispatchedRuns">The redispatched shared runs.</param>
        private static void WriteRecoveredRuntimeWorkInventory(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks,
            IReadOnlyList<AiSharedRunRecord> redispatchedRuns)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(seededWorks);
            ArgumentNullException.ThrowIfNull(redispatchedRuns);

            output.WriteLine("[RECOVERED RUNTIME WORK INVENTORY]");
            output.WriteLine($"FailedRuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"RecoveredCount='{redispatchedRuns.Count}'");

            var index =
                1;

            foreach (var work in seededWorks)
            {
                var recoveredRun =
                    redispatchedRuns.Single(run =>
                        string.Equals(run.SharedRunId, work.SharedRunId, StringComparison.Ordinal));

                output.WriteLine(
                    $"{index:00}. " +
                    $"Kind='{work.Kind}', " +
                    $"SharedRunId='{work.SharedRunId}', " +
                    $"FailedLocalRunId='{work.FailedLocalRunId}', " +
                    $"ReplacementRuntimeInstanceId='{recoveredRun.AssignedRuntimeInstanceId}', " +
                    $"ReplacementLocalRunId='{recoveredRun.LocalRunId}', " +
                    $"ExecutionIdBefore='{work.ExecutionId}', " +
                    $"ExecutionIdAfter='{recoveredRun.ExecutionId}'.");

                index++;
            }
        }

        /// <summary>
        /// Writes the forensics records linked to the recovered failed-runtime inventory.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="records">The recovery forensics records.</param>
        private static void WriteRuntimeRecoveryInventoryForensics(
            ITestOutputHelper output,
            string failedRuntimeInstanceId,
            IReadOnlyList<AiRuntimeRecoveryForensicsReadModel> records)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentNullException.ThrowIfNull(records);

            output.WriteLine("[RUNTIME RECOVERY INVENTORY FORENSICS]");
            output.WriteLine($"FailedRuntimeInstanceId='{failedRuntimeInstanceId}'");
            output.WriteLine($"ForensicsRecordCount='{records.Count}'");

            var index =
                1;

            foreach (var record in records)
            {
                output.WriteLine(
                    $"{index:00}. " +
                    $"ForensicsId='{record.ForensicsId}', " +
                    $"ExecutionId='{record.ExecutionId}', " +
                    $"SharedRunId='{record.SharedRunId}', " +
                    $"TenantId='{record.TenantId}', " +
                    $"Timeline='{string.Join(" -> ", record.Timeline.Select(item => item.EventType))}'.");

                index++;
            }
        }

        private static string? TryResolveRuntimeFailureIncidentId(
            AiRuntimeRecoveryForensicsReadModel record)
        {
            var property =
                record
                    .GetType()
                    .GetProperty("RuntimeFailureIncidentId");

            return property
                ?.GetValue(record)
                ?.ToString();
        }

        private static int CountSeededWork(
            IEnumerable<FailedRuntimeRecoveryGroup> groups)
        {
            return groups.Sum(group => group.SeededWorks.Count);
        }

        private static void AssertRuntimeBelongsToTenant(
            string runtimeInstanceId,
            ProductionTenantScenarioDefinition tenant)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(runtimeInstanceId),
                "Runtime instance id must not be empty.");

            Assert.True(
                runtimeInstanceId.Contains(tenant.TenantId, StringComparison.Ordinal) ||
                runtimeInstanceId.Contains(tenant.RuntimeInstanceIdPrefix, StringComparison.Ordinal),
                $"Runtime instance does not appear to belong to the expected tenant. RuntimeInstanceId='{runtimeInstanceId}', TenantId='{tenant.TenantId}', RuntimeInstanceIdPrefix='{tenant.RuntimeInstanceIdPrefix}'.");
        }

        private static ProductionRuntimeScenarioDefinition CreateConcurrentMultiInstanceRecoveryScenario()
        {
            return CreateConcurrentMultiInstanceRecoveryScenarioCore(
                includeSafeTenant: false);
        }

        private static ProductionRuntimeScenarioDefinition CreateConcurrentSafeTenantRecoveryScenario()
        {
            return CreateConcurrentMultiInstanceRecoveryScenarioCore(
                includeSafeTenant: true);
        }

        private static ProductionRuntimeScenarioDefinition CreateConcurrentMultiInstanceRecoveryScenarioCore(
            bool includeSafeTenant)
        {
            var baseScenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var templateTenant =
                baseScenario.Tenants.Single();

            var tenantA =
                templateTenant with
                {
                    TenantId = "tenant-concurrent-a",
                    TenantGroupId = "tenant-concurrent-a-group",
                    RuntimeInstanceIdPrefix = "tenant-concurrent-a-runtime",
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = 1,
                        StepCount = StepCount,
                        DelayMs = 750,
                        FlakyStepInterval = 0,
                        EnableRetention = true
                    }
                };

            var tenantB =
                templateTenant with
                {
                    TenantId = "tenant-concurrent-b",
                    TenantGroupId = "tenant-concurrent-b-group",
                    RuntimeInstanceIdPrefix = "tenant-concurrent-b-runtime",
                    MaxRuntimeInstances = 2,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    Run = templateTenant.Run with
                    {
                        RunCount = 1,
                        StepCount = StepCount,
                        DelayMs = 750,
                        FlakyStepInterval = 0,
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
                tenants.Add(
                    templateTenant with
                    {
                        TenantId = "tenant-concurrent-c",
                        TenantGroupId = "tenant-concurrent-c-group",
                        RuntimeInstanceIdPrefix = "tenant-concurrent-c-runtime",
                        MaxRuntimeInstances = 1,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 0,
                        Run = templateTenant.Run with
                        {
                            RunCount = 1,
                            StepCount = StepCount,
                            DelayMs = 750,
                            FlakyStepInterval = 0,
                            EnableRetention = true
                        }
                    });
            }

            return baseScenario with
            {
                Name = includeSafeTenant
                    ? "http-process-host-dag-resume-concurrent-runtime-recovery-safe-tenant"
                    : "http-process-host-dag-resume-concurrent-runtime-recovery",
                ControlPlaneIdPrefix = includeSafeTenant
                    ? "http-process-host-concurrent-runtime-recovery-safe-tenant"
                    : "http-process-host-concurrent-runtime-recovery",
                Tenants = tenants.ToArray(),
                PersistenceProfile = ProductionRuntimePersistenceProfile.MongoRedis,
                ObservabilityProfile = ProductionRuntimeObservabilityProfile.DurableMongo,
                HostCreationMode = ProductionRuntimeHostCreationMode.Process,
                SubmitMode = ProductionRuntimeSubmitMode.DirectDispatch,
                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(3),
                CompletionTimeout = TimeSpan.FromMinutes(5),
                Assertions = new ProductionRuntimeScenarioAssertionOptions
                {
                    AssertAllRunsCompleted = true,
                    AssertTenantIsolation = true,
                    AssertScaleOut = true,
                    AssertMaxRuntimeInstances = true,
                    AssertLedger = true,
                    AssertTrace = true,
                    AssertReplayReport = false,
                    AssertReplayLedger = false,
                    AssertReplayTrace = false
                }
            };
        }

    }
}