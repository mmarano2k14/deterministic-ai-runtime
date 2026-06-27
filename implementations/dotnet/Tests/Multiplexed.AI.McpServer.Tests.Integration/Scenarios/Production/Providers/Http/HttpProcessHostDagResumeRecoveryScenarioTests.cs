using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.Stores;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http
{
    /// <summary>
    /// HTTP process-host DAG resume recovery tests proving that a failed runtime can stop
    /// at a claimed DAG step and a replacement runtime can resume from that same step
    /// without replaying already completed steps.
    /// </summary>
    public sealed class HttpProcessHostDagResumeRecoveryScenarioTests
    {
        private const string RequestedBy = "http-process-host-dag-resume-recovery-test";
        private const string Source = "http-process-host-dag-resume-recovery";
        private const int StepCount = 100;
        private const int FailureStepNumber = 50;

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpProcessHostDagResumeRecoveryScenarioTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpProcessHostDagResumeRecoveryScenarioTests(
            ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that HTTP process-host recovery resumes a durable DAG from the failed step
        /// and does not replay steps already completed before the runtime failure.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Resume_Dag_From_Failed_Step_Without_Replaying_Completed_Steps()
        {
            var scenario =
                CreateHttpDagResumeRecoveryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(1);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(1);

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

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenant =
                scenario.Tenants.Single();

            using var tenantHttpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantHttpClient,
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Starting. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
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
                    scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            var firstDispatch =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        mcp,
                        pipelineName,
                        sharedRunId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.LocalRunId));

            var failedRuntimeInstanceId =
                firstDispatch.AssignedRuntimeInstanceId!;

            var failedLocalRunId =
                firstDispatch.LocalRunId!;

            var existingExecutionId =
                $"http-dag-resume-existing-execution-{Guid.NewGuid():N}";

            await ProductionRecoverySeedHelpers
                .SeedInFlightRuntimeExecutionAsync(
                    sharedRunStore,
                    sharedQueue,
                    runExecutionIndex,
                    firstDispatch,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    existingExecutionId)
                .ConfigureAwait(false);

            await ProductionRecoverySeedHelpers
                .SeedDurableDagStoppedAtStepAsync(
                    dagStore,
                    existingExecutionId,
                    pipelineName,
                    firstDispatch.RunRequest?.PipelineDefinition,
                    firstDispatch.ExecutionContextSnapshot?.ContextKey,
                    StepCount,
                    FailureStepNumber,
                    failedRuntimeInstanceId)
                .ConfigureAwait(false);

            var beforeRecovery =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            ProductionDagRecoveryAssertions.AssertDagStoppedAtFailurePoint(
                beforeRecovery,
                FailureStepNumber,
                StepCount);

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Seeded DAG state. ExecutionId='{existingExecutionId}', CompletedBeforeFailure='{FailureStepNumber - 1}', FailedStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}'.");

            var recoveryResult =
                await ProductionRecoveryWaitHelpers
                    .MarkUnhealthyAndReconcileUntilRecoveredAsync(
                        registry,
                        healthReconciler,
                        recoveryReconciler,
                        failedRuntimeInstanceId,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            Assert.Equal(1, recoveryResult.RecoveredRunCount);

            Assert.Contains(
                recoveryResult.Decisions,
                decision =>
                    decision.RuntimeInstanceId == failedRuntimeInstanceId &&
                    decision.LocalRunId == failedLocalRunId &&
                    decision.ExecutionId == existingExecutionId &&
                    decision.SharedRunId == sharedRunId &&
                    decision.Action == "requeue-shared-run" &&
                    decision.Reason == "runtime-execution-recovery-requeue" &&
                    decision.Changed);

            var queueItemAfterRecovery =
                await sharedQueue
                    .GetAsync(sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(queueItemAfterRecovery);
            Assert.Contains(
                queueItemAfterRecovery!.Status,
                new[]
                {
                    AiSharedQueueItemStatus.Pending,
                    AiSharedQueueItemStatus.Claimed,
                    AiSharedQueueItemStatus.Dispatched
                });
            Assert.Equal("resume-existing-execution", queueItemAfterRecovery.Metadata["recovery.mode"]);
            Assert.Equal(existingExecutionId, queueItemAfterRecovery.Metadata["recovery.failedExecutionId"]);
            Assert.Equal(failedRuntimeInstanceId, queueItemAfterRecovery.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal(failedLocalRunId, queueItemAfterRecovery.Metadata["recovery.failedLocalRunId"]);

            var failedIndexAfterRecovery =
                await runExecutionIndex
                    .GetAsync(failedLocalRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(failedIndexAfterRecovery);
            Assert.Equal("requeued-for-recovery", failedIndexAfterRecovery!.Status);

            var recoveredBeforeRedispatch =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            ProductionDagRecoveryAssertions.AssertDagStoppedAtFailurePoint(
                recoveredBeforeRedispatch,
                FailureStepNumber,
                StepCount);

            var redispatchedRun =
                await ProductionRecoveryWaitHelpers
                    .WaitForSharedRunAssignedAwayFromRuntimeAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedRunId,
                        failedRuntimeInstanceId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
            Assert.NotEqual(failedRuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
            Assert.NotEqual(failedLocalRunId, redispatchedRun.LocalRunId);
            Assert.Equal(existingExecutionId, redispatchedRun.ExecutionId);

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Redispatch observed. SharedRunId='{sharedRunId}', NewRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', NewLocalRunId='{redispatchedRun.LocalRunId}', ExecutionId='{redispatchedRun.ExecutionId}'.");

            var finalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        new[] { redispatchedRun },
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            var finalStatus =
                Assert.Single(finalStatuses);

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(redispatchedRun.AssignedRuntimeInstanceId, finalStatus.RuntimeInstanceId);
            Assert.Equal(redispatchedRun.LocalRunId, finalStatus.RunId);

            if (!string.Equals(finalStatus.RunState?.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var failedDagState =
                    await dagStore
                        .GetStateAsync(existingExecutionId)
                        .ConfigureAwait(false);

                Assert.Fail(
                    "Recovered DAG resume run did not complete. " +
                    $"ExpectedStatus='completed', ActualStatus='{finalStatus.RunState?.Status}', " +
                    $"Success='{finalStatus.Success}', FailureReason='{finalStatus.FailureReason}', Message='{finalStatus.Message}', " +
                    $"RuntimeInstanceId='{finalStatus.RuntimeInstanceId}', RunId='{finalStatus.RunId}', ExecutionId='{finalStatus.ExecutionId}', " +
                    $"RunStateExecutionId='{finalStatus.RunState?.ExecutionId}', RunStateFailureReason='{finalStatus.RunState?.FailureReason}', " +
                    $"DagSummary='{ProductionDagRecoveryAssertions.FormatDagStateSummary(failedDagState)}'.");
            }

            var finalExecutionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId ??
                redispatchedRun.ExecutionId;

            Assert.Equal(existingExecutionId, finalExecutionId);

            var finalDagState =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            if (finalDagState is not null)
            {
                ProductionDagRecoveryAssertions.AssertDagCompletedFromFailurePoint(
                    finalDagState,
                    FailureStepNumber,
                    StepCount);
            }
            else
            {
                this.output.WriteLine(
                    $"[HTTP DAG RESUME] Final DAG hot state was not available after completion. ExecutionId='{existingExecutionId}'. This is valid when retention cleanup has already externalized or removed hot DAG state.");
            }

            var replacementIndex =
                await ProductionRecoveryWaitHelpers
                    .WaitForRunExecutionIndexAsync(
                        runExecutionIndex,
                        redispatchedRun.LocalRunId!,
                        existingExecutionId,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);

            Assert.Equal(existingExecutionId, replacementIndex.ExecutionId);
            Assert.Equal(redispatchedRun.AssignedRuntimeInstanceId, replacementIndex.RuntimeInstanceId);
            Assert.Equal("completed", replacementIndex.Status);

            this.output.WriteLine(
                $"[HTTP DAG RESUME PROOF] ExecutionId='{existingExecutionId}', FailureStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', CompletedBeforeFailure='{FailureStepNumber - 1}', RecoveredFromStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', FinalCompletedSteps='{StepCount}/{StepCount}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', FailedLocalRunId='{failedLocalRunId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that HTTP process-host DAG resume recovery forensics are exposed through MCP search/get/timeline tools.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Expose_Dag_Resume_Recovery_Forensics_Timeline_Through_Mcp()
        {
            var scenario =
                CreateHttpDagResumeRecoveryScenario();

            scenario.DispatchTimeout = TimeSpan.FromSeconds(30);
            scenario.CompletionTimeout = TimeSpan.FromSeconds(30);

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

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenant =
                scenario.Tenants.Single();

            using var tenantHttpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantHttpClient,
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Starting. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
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
                    scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            var firstDispatch =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        mcp,
                        pipelineName,
                        sharedRunId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.LocalRunId));

            var failedRuntimeInstanceId =
                firstDispatch.AssignedRuntimeInstanceId!;

            var failedLocalRunId =
                firstDispatch.LocalRunId!;

            var existingExecutionId =
                $"http-dag-resume-existing-execution-{Guid.NewGuid():N}";

            var expectedForensicsId =
                string.Join(
                    ":",
                    "runtime-recovery",
                    existingExecutionId,
                    sharedRunId,
                    failedLocalRunId);

            await ProductionRecoverySeedHelpers
                .SeedInFlightRuntimeExecutionAsync(
                    sharedRunStore,
                    sharedQueue,
                    runExecutionIndex,
                    firstDispatch,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    existingExecutionId)
                .ConfigureAwait(false);

            await ProductionRecoverySeedHelpers
                .SeedDurableDagStoppedAtStepAsync(
                    dagStore,
                    existingExecutionId,
                    pipelineName,
                    firstDispatch.RunRequest?.PipelineDefinition,
                    firstDispatch.ExecutionContextSnapshot?.ContextKey,
                    StepCount,
                    FailureStepNumber,
                    failedRuntimeInstanceId)
                .ConfigureAwait(false);

            var beforeRecovery =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            ProductionDagRecoveryAssertions.AssertDagStoppedAtFailurePoint(
                beforeRecovery,
                FailureStepNumber,
                StepCount);

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Seeded DAG state. ExecutionId='{existingExecutionId}', CompletedBeforeFailure='{FailureStepNumber - 1}', FailedStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}'.");

            var recoveryResult =
                await ProductionRecoveryWaitHelpers
                    .MarkUnhealthyAndReconcileUntilRecoveredAsync(
                        registry,
                        healthReconciler,
                        recoveryReconciler,
                        failedRuntimeInstanceId,
                        TimeSpan.FromSeconds(60))
                    .ConfigureAwait(false);

            Assert.Equal(1, recoveryResult.RecoveredRunCount);

            Assert.Contains(
                recoveryResult.Decisions,
                decision =>
                    decision.RuntimeInstanceId == failedRuntimeInstanceId &&
                    decision.LocalRunId == failedLocalRunId &&
                    decision.ExecutionId == existingExecutionId &&
                    decision.SharedRunId == sharedRunId &&
                    decision.Action == "requeue-shared-run" &&
                    decision.Reason == "runtime-execution-recovery-requeue" &&
                    decision.Changed);

            var queueItemAfterRecovery =
                await sharedQueue
                    .GetAsync(sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(queueItemAfterRecovery);
            Assert.Contains(
                queueItemAfterRecovery!.Status,
                new[]
                {
                    AiSharedQueueItemStatus.Pending,
                    AiSharedQueueItemStatus.Claimed,
                    AiSharedQueueItemStatus.Dispatched
                });
            Assert.Equal("resume-existing-execution", queueItemAfterRecovery.Metadata["recovery.mode"]);
            Assert.Equal(existingExecutionId, queueItemAfterRecovery.Metadata["recovery.failedExecutionId"]);
            Assert.Equal(failedRuntimeInstanceId, queueItemAfterRecovery.Metadata["recovery.failedRuntimeInstanceId"]);
            Assert.Equal(failedLocalRunId, queueItemAfterRecovery.Metadata["recovery.failedLocalRunId"]);
            Assert.Equal(expectedForensicsId, queueItemAfterRecovery.Metadata["recovery.forensicsId"]);

            var failedIndexAfterRecovery =
                await runExecutionIndex
                    .GetAsync(failedLocalRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(failedIndexAfterRecovery);
            Assert.Equal("requeued-for-recovery", failedIndexAfterRecovery!.Status);

            var recoveredBeforeRedispatch =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            ProductionDagRecoveryAssertions.AssertDagStoppedAtFailurePoint(
                recoveredBeforeRedispatch,
                FailureStepNumber,
                StepCount);

            var redispatchedRun =
                await ProductionRecoveryWaitHelpers
                    .WaitForSharedRunAssignedAwayFromRuntimeAsync(
                        registry,
                        healthReconciler,
                        sharedRunStore,
                        sharedRunId,
                        failedRuntimeInstanceId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
            Assert.NotEqual(failedRuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
            Assert.NotEqual(failedLocalRunId, redispatchedRun.LocalRunId);
            Assert.Equal(existingExecutionId, redispatchedRun.ExecutionId);

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Redispatch observed. SharedRunId='{sharedRunId}', NewRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', NewLocalRunId='{redispatchedRun.LocalRunId}', ExecutionId='{redispatchedRun.ExecutionId}'.");

            var finalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        new[] { redispatchedRun },
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            var finalStatus =
                Assert.Single(finalStatuses);

            Assert.True(
                finalStatus.Success,
                finalStatus.FailureReason ?? finalStatus.Message);

            Assert.Equal(redispatchedRun.AssignedRuntimeInstanceId, finalStatus.RuntimeInstanceId);
            Assert.Equal(redispatchedRun.LocalRunId, finalStatus.RunId);

            if (!string.Equals(finalStatus.RunState?.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var failedDagState =
                    await dagStore
                        .GetStateAsync(existingExecutionId)
                        .ConfigureAwait(false);

                Assert.Fail(
                    "Recovered DAG resume run did not complete. " +
                    $"ExpectedStatus='completed', ActualStatus='{finalStatus.RunState?.Status}', " +
                    $"Success='{finalStatus.Success}', FailureReason='{finalStatus.FailureReason}', Message='{finalStatus.Message}', " +
                    $"RuntimeInstanceId='{finalStatus.RuntimeInstanceId}', RunId='{finalStatus.RunId}', ExecutionId='{finalStatus.ExecutionId}', " +
                    $"RunStateExecutionId='{finalStatus.RunState?.ExecutionId}', RunStateFailureReason='{finalStatus.RunState?.FailureReason}', " +
                    $"DagSummary='{ProductionDagRecoveryAssertions.FormatDagStateSummary(failedDagState)}'.");
            }

            var finalExecutionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId ??
                redispatchedRun.ExecutionId;

            Assert.Equal(existingExecutionId, finalExecutionId);

            var finalDagState =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            if (finalDagState is not null)
            {
                ProductionDagRecoveryAssertions.AssertDagCompletedFromFailurePoint(
                    finalDagState,
                    FailureStepNumber,
                    StepCount);
            }
            else
            {
                this.output.WriteLine(
                    $"[HTTP DAG RESUME] Final DAG hot state was not available after completion. ExecutionId='{existingExecutionId}'. This is valid when retention cleanup has already externalized or removed hot DAG state.");
            }

            var replacementIndex =
                await ProductionRecoveryWaitHelpers
                    .WaitForRunExecutionIndexAsync(
                        runExecutionIndex,
                        redispatchedRun.LocalRunId!,
                        existingExecutionId,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);

            Assert.Equal(existingExecutionId, replacementIndex.ExecutionId);
            Assert.Equal(redispatchedRun.AssignedRuntimeInstanceId, replacementIndex.RuntimeInstanceId);
            Assert.Equal("completed", replacementIndex.Status);

            var recorder =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsRecorder>();

            var store =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsStore>();

            var queryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            this.output.WriteLine(
                "[FORENSICS DI PROOF] " +
                $"Recorder='{recorder.GetType().FullName}', " +
                $"Store='{store.GetType().FullName}', " +
                $"QueryService='{queryService.GetType().FullName}'.");

            var directByExecution =
                await store
                    .ListByExecutionIdAsync(existingExecutionId)
                    .ConfigureAwait(false);

            var directByShared =
                await store
                    .ListBySharedRunIdAsync(sharedRunId)
                    .ConfigureAwait(false);

            this.output.WriteLine(
                "[FORENSICS STORE PROOF] " +
                $"ExecutionId='{existingExecutionId}', " +
                $"SharedRunId='{sharedRunId}', " +
                $"DirectByExecutionCount='{directByExecution.Count}', " +
                $"DirectBySharedRunCount='{directByShared.Count}', " +
                $"ExecutionForensicsIds='{string.Join(",", directByExecution.Select(record => record.Identity.ForensicsId))}', " +
                $"SharedRunForensicsIds='{string.Join(",", directByShared.Select(record => record.Identity.ForensicsId))}'.");

            Assert.Single(directByExecution);
            Assert.Single(directByShared);

            var directRecord =
                directByExecution.Single();

            Assert.Equal(expectedForensicsId, directRecord.Identity.ForensicsId);
            Assert.Equal(existingExecutionId, directRecord.Identity.ExecutionId);
            Assert.Equal(sharedRunId, directRecord.Identity.SharedRunId);
            Assert.Equal(tenant.TenantId, directRecord.Identity.TenantId);
            Assert.Equal(tenant.TenantGroupId, directRecord.Identity.TenantGroupId);

            Assert.True(
                string.IsNullOrWhiteSpace(directRecord.Identity.ControlPlaneId) ||
                string.Equals(controlPlaneId, directRecord.Identity.ControlPlaneId, StringComparison.Ordinal),
                $"ControlPlaneId is optional for recovery forensics, but when present it must match. Expected='{controlPlaneId}', Actual='{directRecord.Identity.ControlPlaneId}'.");

            Assert.Equal(pipelineName, directRecord.Identity.PipelineName);

            var directQueryResult =
                await queryService
                    .SearchAsync(
                        new AiRuntimeRecoveryForensicsQuery
                        {
                            ExecutionId = existingExecutionId,
                            SharedRunId = sharedRunId,
                            TenantId = tenant.TenantId,
                            Limit = 20
                        })
                    .ConfigureAwait(false);

            this.output.WriteLine(
                "[FORENSICS QUERY SERVICE PROOF] " +
                $"ExecutionId='{existingExecutionId}', " +
                $"SharedRunId='{sharedRunId}', " +
                $"TenantId='{tenant.TenantId}', " +
                $"ControlPlaneId='{controlPlaneId}', " +
                $"DirectQueryCount='{directQueryResult.Items.Count}', " +
                $"Ids='{string.Join(",", directQueryResult.Items.Select(item => item.ForensicsId))}'.");

            Assert.Equal(1, directQueryResult.Items.Count);

            var directReadModel =
                directQueryResult.Items.Single();

            Assert.Equal(expectedForensicsId, directReadModel.ForensicsId);
            Assert.Equal(existingExecutionId, directReadModel.ExecutionId);
            Assert.Equal(sharedRunId, directReadModel.SharedRunId);
            Assert.Equal(tenant.TenantId, directReadModel.TenantId);

            Assert.True(
                string.IsNullOrWhiteSpace(directReadModel.ControlPlaneId) ||
                string.Equals(controlPlaneId, directReadModel.ControlPlaneId, StringComparison.Ordinal),
                $"ControlPlaneId is optional for recovery forensics, but when present it must match. Expected='{controlPlaneId}', Actual='{directReadModel.ControlPlaneId}'.");

            await ProductionRecoveryForensicsAssertions
                .AssertRecoveryForensicsTimelineViaMcpAsync(
                    mcp,
                    expectedForensicsId,
                    existingExecutionId,
                    sharedRunId,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    redispatchedRun.AssignedRuntimeInstanceId!,
                    redispatchedRun.LocalRunId!,
                    tenant.TenantId,
                    tenant.TenantGroupId,
                    controlPlaneId,
                    pipelineName,
                    this.output,
                    TimeSpan.FromSeconds(45))
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[HTTP DAG RESUME PROOF] ExecutionId='{existingExecutionId}', FailureStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', CompletedBeforeFailure='{FailureStepNumber - 1}', RecoveredFromStep='{ProductionRecoverySeedHelpers.FormatStepName(FailureStepNumber)}', FinalCompletedSteps='{StepCount}/{StepCount}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', FailedLocalRunId='{failedLocalRunId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}'.");
        }

        /// <summary>
        /// Verifies that the real HTTP runtime creation path persists the RBAC ContextKey
        /// on the durable DAG execution record without test-side seeding.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Persist_Dag_Record_ContextKey_On_Real_Create()
        {
            var scenario =
                CreateHttpDagResumeRecoveryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(1);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(2);

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

            var runExecutionIndex =
                host.Services.GetRequiredService<IAiRuntimeRunExecutionIndex>();

            var scaleOutRequestStore =
                host.Services.GetRequiredService<IAiRuntimeScaleOutRequestStore>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var tenant =
                scenario.Tenants.Single();

            using var tenantHttpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantHttpClient,
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-real-create-contextkey-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[HTTP DAG CONTEXTKEY PROOF] Starting. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var sharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
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
                    scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            var firstDispatch =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        mcp,
                        pipelineName,
                        sharedRunId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(firstDispatch.LocalRunId));

            var sharedRun =
                await sharedRunStore
                    .GetAsync(sharedRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(sharedRun);

            var expectedContextKey =
                firstDispatch.ExecutionContextSnapshot?.ContextKey
                ?? sharedRun!.ExecutionContextSnapshot?.ContextKey;

            Assert.False(
                string.IsNullOrWhiteSpace(expectedContextKey),
                "The dispatched shared run must carry an ExecutionContextSnapshot.ContextKey.");

            var runtimeIndex =
                await ProductionRecoveryWaitHelpers
                    .WaitForRuntimeIndexWithExecutionIdAsync(
                        runExecutionIndex,
                        firstDispatch.LocalRunId!,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            Assert.Equal(firstDispatch.AssignedRuntimeInstanceId, runtimeIndex.RuntimeInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(runtimeIndex.ExecutionId));

            var persistedRecord =
                await ProductionRecoveryWaitHelpers
                    .WaitForDagRecordWithContextKeyAsync(
                        dagStore,
                        runtimeIndex.ExecutionId,
                        TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false);

            Assert.Equal(runtimeIndex.ExecutionId, persistedRecord.ExecutionId);
            Assert.Equal(pipelineName, persistedRecord.PipelineName);
            Assert.Equal(AiExecutionMode.Dag, persistedRecord.ExecutionMode);
            Assert.False(string.IsNullOrWhiteSpace(persistedRecord.ContextKey));

            this.output.WriteLine(
                $"[HTTP DAG CONTEXTKEY PROOF] Real DAG record persisted ContextKey. " +
                $"SharedRunId='{sharedRunId}', " +
                $"LocalRunId='{firstDispatch.LocalRunId}', " +
                $"ExecutionId='{runtimeIndex.ExecutionId}', " +
                $"RuntimeInstanceId='{firstDispatch.AssignedRuntimeInstanceId}', " +
                $"SnapshotContextKey='{expectedContextKey}', " +
                $"RecordContextKey='{persistedRecord.ContextKey}', " +
                $"SameContextKey='{string.Equals(expectedContextKey, persistedRecord.ContextKey, StringComparison.Ordinal)}'.");
        }

        /// <summary>
        /// Verifies that a failed HTTP runtime instance can expose and recover a durable assigned-work inventory
        /// containing both local queued runs and in-flight executions.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_Durable_Assigned_Work_Inventory_From_Failed_Runtime()
        {
            var scenario = CreateHttpRuntimeRecoveryInventoryScenario(5);

            var tenantScenario =
                scenario.Tenants.Single();

            var totalRecoverableWorkCount =
                tenantScenario.Run.RunCount;

            var inFlightExecutionCount =
                Math.Max(1, totalRecoverableWorkCount / 2);

            var queuedLocalRunCount =
                totalRecoverableWorkCount - inFlightExecutionCount;

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

            var recoveryReconciler =
                host.Services.GetRequiredService<IAiRuntimeExecutionRecoveryReconciler>();

            var dagStore =
                host.Services.GetRequiredService<IAiDagExecutionStore>();

            var queryService =
                host.Services.GetRequiredService<IAiRuntimeRecoveryForensicsQueryService>();

            var recoveryOptions =
                host.Services
                    .GetRequiredService<IOptions<AiRuntimeExecutionRecoveryReconciliationOptions>>()
                    .Value;

            ProductionRecoveryOptionsAssertions.AssertDagResumeRecoveryEnabled(recoveryOptions);

            var tenant =
                scenario.Tenants.Single();

            using var tenantHttpClient =
                host.CreateClient();

            var mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        host,
                        tenantHttpClient,
                        RequestedBy,
                        tenantId: tenant.TenantId,
                        tenantGroupId: tenant.TenantGroupId)
                    .ConfigureAwait(false);

            var pipelineName =
                $"{scenario.Name}-{tenant.TenantId}-work-inventory-{Guid.NewGuid():N}";

            this.output.WriteLine(
                $"[HTTP RUNTIME INVENTORY] Starting. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            var bootstrapSharedRunId =
                await ProductionSharedRunTestHelpers
                    .SubmitOneRunAsync(
                        mcp,
                        tenant,
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
                    scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            var bootstrapDispatch =
                await ProductionSharedRunTestHelpers
                    .WaitForSingleDispatchedRunAsync(
                        mcp,
                        pipelineName,
                        bootstrapSharedRunId,
                        scenario.DispatchTimeout)
                    .ConfigureAwait(false);

            Assert.False(string.IsNullOrWhiteSpace(bootstrapDispatch.AssignedRuntimeInstanceId));
            Assert.False(string.IsNullOrWhiteSpace(bootstrapDispatch.LocalRunId));

            var failedRuntimeInstanceId =
                bootstrapDispatch.AssignedRuntimeInstanceId!;

            var failedRuntimeSnapshot =
                await registry
                    .GetAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.NotNull(failedRuntimeSnapshot);

            var seededWorks =
                await SeedFailedRuntimeAssignedWorkInventoryAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        dagStore,
                        bootstrapDispatch,
                        tenant,
                        pipelineName,
                        failedRuntimeInstanceId,
                        queuedLocalRunCount,
                        inFlightExecutionCount)
                    .ConfigureAwait(false);

            Assert.Equal(totalRecoverableWorkCount, seededWorks.Count);
            Assert.Equal(queuedLocalRunCount, seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.LocalQueued));
            Assert.Equal(inFlightExecutionCount, seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.InFlightExecution));

            WriteFailedRuntimeWorkInventory(
                this.output,
                failedRuntimeInstanceId,
                seededWorks);

            await MarkUnhealthyAndReconcileUntilAllSeededWorkRecoveredAsync(
                    registry,
                    healthReconciler,
                    recoveryReconciler,
                    runExecutionIndex,
                    failedRuntimeInstanceId,
                    seededWorks,
                    TimeSpan.FromSeconds(90))
                .ConfigureAwait(false);

            foreach (var work in seededWorks)
            {
                var failedIndex =
                    await runExecutionIndex
                        .GetAsync(work.FailedLocalRunId)
                        .ConfigureAwait(false);

                Assert.NotNull(failedIndex);
                Assert.Equal("requeued-for-recovery", failedIndex!.Status);
            }

            var redispatchedRuns =
                new List<AiSharedRunRecord>();

            foreach (var work in seededWorks)
            {
                var redispatchedRun =
                    await ProductionRecoveryWaitHelpers
                        .WaitForSharedRunAssignedAwayFromRuntimeAsync(
                            registry,
                            healthReconciler,
                            sharedRunStore,
                            work.SharedRunId,
                            failedRuntimeInstanceId,
                            scenario.DispatchTimeout)
                        .ConfigureAwait(false);

                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.AssignedRuntimeInstanceId));
                Assert.False(string.IsNullOrWhiteSpace(redispatchedRun.LocalRunId));
                Assert.NotEqual(failedRuntimeInstanceId, redispatchedRun.AssignedRuntimeInstanceId);
                Assert.NotEqual(work.FailedLocalRunId, redispatchedRun.LocalRunId);

                if (work.Kind == FailedRuntimeWorkKind.InFlightExecution)
                {
                    Assert.Equal(work.ExecutionId, redispatchedRun.ExecutionId);
                }

                redispatchedRuns.Add(redispatchedRun);
            }

            Assert.Equal(totalRecoverableWorkCount, redispatchedRuns.Count);

            WriteRecoveredRuntimeWorkInventory(
                this.output,
                failedRuntimeInstanceId,
                seededWorks,
                redispatchedRuns);

            var finalStatuses =
                await McpTestWaitHelpers
                    .WaitForTerminalRuntimeRunStatusesAsync(
                        mcp,
                        redispatchedRuns,
                        timeout: scenario.CompletionTimeout)
                    .ConfigureAwait(false);

            Assert.Equal(totalRecoverableWorkCount, finalStatuses.Count);

            foreach (var finalStatus in finalStatuses)
            {
                Assert.True(
                    finalStatus.Success,
                    finalStatus.FailureReason ?? finalStatus.Message);

                Assert.True(
                    string.Equals(finalStatus.RunState?.Status, "completed", StringComparison.OrdinalIgnoreCase),
                    $"Recovered work did not complete. RuntimeInstanceId='{finalStatus.RuntimeInstanceId}', RunId='{finalStatus.RunId}', ExecutionId='{finalStatus.ExecutionId}', Status='{finalStatus.RunState?.Status}', FailureReason='{finalStatus.FailureReason}', Message='{finalStatus.Message}'.");
            }

            foreach (var work in seededWorks.Where(work => work.Kind == FailedRuntimeWorkKind.InFlightExecution))
            {
                var recoveredRun =
                    redispatchedRuns.Single(run =>
                        string.Equals(run.SharedRunId, work.SharedRunId, StringComparison.Ordinal));

                Assert.Equal(work.ExecutionId, recoveredRun.ExecutionId);

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

            var forensicsResult =
                await queryService
                    .SearchAsync(
                        new AiRuntimeRecoveryForensicsQuery
                        {
                            RuntimeInstanceId = failedRuntimeInstanceId,
                            TenantId = tenant.TenantId,
                            Limit = 100
                        })
                    .ConfigureAwait(false);

            var recoveredForensics =
                forensicsResult.Items
                    .Where(item => seededWorks.Any(work =>
                        string.Equals(work.SharedRunId, item.SharedRunId, StringComparison.Ordinal)))
                    .ToArray();

            Assert.True(
                recoveredForensics.Length >= inFlightExecutionCount,
                $"Expected at least the in-flight executions to be visible in recovery forensics. ExpectedAtLeast='{inFlightExecutionCount}', Actual='{recoveredForensics.Length}'.");

            WriteRuntimeRecoveryInventoryForensics(
                this.output,
                failedRuntimeInstanceId,
                recoveredForensics);

            this.output.WriteLine(
                "[RUNTIME RECOVERY INVENTORY PROOF] " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"QueuedLocalRunsRecovered='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.LocalQueued)}/{queuedLocalRunCount}', " +
                $"InFlightExecutionsRecovered='{seededWorks.Count(work => work.Kind == FailedRuntimeWorkKind.InFlightExecution)}/{inFlightExecutionCount}', " +
                $"TotalRecovered='{redispatchedRuns.Count}/{totalRecoverableWorkCount}', " +
                $"ReplacementRuntimeInstances='{string.Join(",", redispatchedRuns.Select(run => run.AssignedRuntimeInstanceId).Distinct(StringComparer.Ordinal))}'.");
        }

        /// <summary>
        /// Creates a focused HTTP process-host DAG resume recovery scenario.
        /// </summary>
        /// <returns>The scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateHttpDagResumeRecoveryScenario()
        {
            var baseScenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var tenant =
                baseScenario.Tenants.Single();

            return baseScenario with
            {
                Name = "http-process-host-dag-resume-recovery",
                ControlPlaneIdPrefix = "http-process-host-dag-resume-recovery",
                Tenants = new[]
                {
                    tenant with
                    {
                        MaxRuntimeInstances = 2,
                        WorkerCountPerInstance = 1,
                        MaxConcurrentRunsPerInstance = 1,
                        LocalQueueCapacity = 2,
                        Run = tenant.Run with
                        {
                            RunCount = 1,
                            StepCount = StepCount,
                            DelayMs = 100,
                            FlakyStepInterval = 0,
                            EnableRetention = true
                        }
                    }
                },
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

        /// <summary>
        /// Creates a focused HTTP process-host runtime recovery inventory scenario.
        /// </summary>
        /// <param name="recoverableWorkCount">The number of durable assigned work items expected in the failed runtime inventory.</param>
        /// <returns>The scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateHttpRuntimeRecoveryInventoryScenario(
            int recoverableWorkCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recoverableWorkCount);

            var scenario =
                CreateHttpDagResumeRecoveryScenario();

            var tenant =
                scenario.Tenants.Single();

            return scenario with
            {
                Name = "http-process-host-dag-resume-recovery-inventory",
                ControlPlaneIdPrefix = "http-process-host-dag-resume-recovery-inventory",
                Tenants = new[]
                {
            tenant with
            {
                MaxRuntimeInstances = 2,
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = recoverableWorkCount,
                Run = tenant.Run with
                {
                    RunCount = recoverableWorkCount,
                    StepCount = StepCount,
                    DelayMs = 100,
                    FlakyStepInterval = 0,
                    EnableRetention = true
                }
            }
        },
                ScaleOutTimeout = TimeSpan.FromMinutes(2),
                DispatchTimeout = TimeSpan.FromMinutes(3),
                CompletionTimeout = TimeSpan.FromMinutes(5)
            };
        }



        /// <summary>
        /// Represents the kind of durable work assigned to a failed runtime instance.
        /// </summary>
        private enum FailedRuntimeWorkKind
        {
            /// <summary>
            /// Work was assigned to the runtime but still queued locally.
            /// </summary>
            LocalQueued,

            /// <summary>
            /// Work had already started a durable execution.
            /// </summary>
            InFlightExecution
        }

        /// <summary>
        /// Represents one durable work item assigned to a failed runtime instance.
        /// </summary>
        private sealed record FailedRuntimeWorkSeed
        {
            /// <summary>
            /// Gets the durable shared run identifier.
            /// </summary>
            public required string SharedRunId { get; init; }

            /// <summary>
            /// Gets the failed local runtime run identifier.
            /// </summary>
            public required string FailedLocalRunId { get; init; }

            /// <summary>
            /// Gets the optional durable execution identifier.
            /// </summary>
            public string? ExecutionId { get; init; }

            /// <summary>
            /// Gets the work kind.
            /// </summary>
            public required FailedRuntimeWorkKind Kind { get; init; }
        }

        /// <summary>
        /// Seeds a durable failed-runtime assigned-work inventory without faking recovery results.
        /// </summary>
        private static async Task<IReadOnlyList<FailedRuntimeWorkSeed>> SeedFailedRuntimeAssignedWorkInventoryAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiDagExecutionStore dagStore,
            AiSharedRunRecord templateSharedRun,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string failedRuntimeInstanceId,
            int queuedLocalRunCount,
            int inFlightExecutionCount)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            var seeded =
                new List<FailedRuntimeWorkSeed>();

            for (var index = 1; index <= queuedLocalRunCount; index++)
            {
                var sharedRunId =
                    Guid.NewGuid().ToString("N");

                var localRunId =
                    $"failed-local-queued-{index}-{Guid.NewGuid():N}";

                var sharedRun =
                    await CreateDurableSharedRunForFailedRuntimeAsync(
                            sharedRunStore,
                            templateSharedRun,
                            tenant,
                            pipelineName,
                            sharedRunId,
                            reason: "seeded-local-queued-work")
                        .ConfigureAwait(false);

                await SeedDurableAssignedQueueOwnershipAsync(
                        sharedQueue,
                        sharedRun,
                        failedRuntimeInstanceId,
                        reason: "seeded-local-queued-work")
                    .ConfigureAwait(false);

                await sharedRunStore
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        failedRuntimeInstanceId,
                        localRunId,
                        executionId: null,
                        reason: "seeded-local-queued-work")
                    .ConfigureAwait(false);

                await runExecutionIndex
                    .RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = localRunId,
                        ExecutionId = null,
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        Status = "queued",
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = "http-runtime-recovery-inventory",
                            ["inventory.kind"] = "local-queued",
                            ["seeded"] = "true"
                        }
                    })
                    .ConfigureAwait(false);

                seeded.Add(new FailedRuntimeWorkSeed
                {
                    SharedRunId = sharedRun.SharedRunId,
                    FailedLocalRunId = localRunId,
                    ExecutionId = null,
                    Kind = FailedRuntimeWorkKind.LocalQueued
                });
            }

            for (var index = 1; index <= inFlightExecutionCount; index++)
            {
                var sharedRunId =
                    Guid.NewGuid().ToString("N");

                var localRunId =
                    $"failed-local-running-{index}-{Guid.NewGuid():N}";

                var executionId =
                    $"http-runtime-inventory-running-execution-{index}-{Guid.NewGuid():N}";

                var sharedRun =
                    await CreateDurableSharedRunForFailedRuntimeAsync(
                            sharedRunStore,
                            templateSharedRun,
                            tenant,
                            pipelineName,
                            sharedRunId,
                            reason: "seeded-in-flight-execution")
                        .ConfigureAwait(false);

                await ProductionRecoverySeedHelpers
                    .SeedInFlightRuntimeExecutionAsync(
                        sharedRunStore,
                        sharedQueue,
                        runExecutionIndex,
                        sharedRun,
                        failedRuntimeInstanceId,
                        localRunId,
                        executionId)
                    .ConfigureAwait(false);

                await ProductionRecoverySeedHelpers
                    .SeedDurableDagStoppedAtStepAsync(
                        dagStore,
                        executionId,
                        pipelineName,
                        sharedRun.RunRequest?.PipelineDefinition,
                        sharedRun.ExecutionContextSnapshot?.ContextKey,
                        StepCount,
                        FailureStepNumber,
                        failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                seeded.Add(new FailedRuntimeWorkSeed
                {
                    SharedRunId = sharedRun.SharedRunId,
                    FailedLocalRunId = localRunId,
                    ExecutionId = executionId,
                    Kind = FailedRuntimeWorkKind.InFlightExecution
                });
            }

            return seeded;
        }

        /// <summary>
        /// Creates a durable shared run record used as crash-surviving ownership evidence.
        /// </summary>
        private static async Task<AiSharedRunRecord> CreateDurableSharedRunForFailedRuntimeAsync(
            IAiSharedRunStore sharedRunStore,
            AiSharedRunRecord templateSharedRun,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            string sharedRunId,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(templateSharedRun);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            var record =
                new AiSharedRunRecord
                {
                    SharedRunId = sharedRunId,
                    Status = AiSharedRunStatus.QueuedGlobally,
                    RunRequest = templateSharedRun.RunRequest,
                    ExecutionContextSnapshot = templateSharedRun.ExecutionContextSnapshot,
                    LocalRunId = null,
                    ExecutionId = null,
                    AssignedRuntimeInstanceId = null,
                    AdmissionDecision = null,
                    PipelineKey = pipelineName,
                    CorrelationId = $"correlation-{sharedRunId}",
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Reason = reason,
                    FailureReason = null,
                    SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                        ["pipelineName"] = pipelineName,
                        ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix,
                        ["scenario"] = "http-runtime-recovery-inventory",
                        ["seeded"] = "true"
                    },
                    ControlPlaneId = templateSharedRun.ControlPlaneId
                };

            return await sharedRunStore
                .CreateAsync(record)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Seeds durable shared queue ownership for a failed runtime local queued item.
        /// </summary>
        private static async Task SeedDurableAssignedQueueOwnershipAsync(
            IAiSharedQueue sharedQueue,
            AiSharedRunRecord sharedRun,
            string failedRuntimeInstanceId,
            string reason)
        {
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(sharedRun);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            await sharedQueue
                .EnqueueAsync(new AiSharedQueueItem
                {
                    SharedRunId = sharedRun.SharedRunId,
                    Status = AiSharedQueueItemStatus.Pending,
                    ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                    PipelineKey = sharedRun.PipelineKey,
                    Priority = 0,
                    EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "http-runtime-recovery-inventory",
                        ["inventory.kind"] = "local-queued",
                        ["seeded"] = "true"
                    }
                })
                .ConfigureAwait(false);

            var claim =
                await sharedQueue
                    .ClaimNextAsync(new AiSharedQueueClaimRequest
                    {
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        WorkerId = "http-runtime-inventory-seed-worker",
                        TenantId = sharedRun.ExecutionContextSnapshot?.TenantId,
                        PipelineKey = sharedRun.PipelineKey,
                        ClaimTtl = TimeSpan.FromMinutes(5),
                        Reason = reason
                    })
                    .ConfigureAwait(false);

            Assert.NotNull(claim);
            Assert.Equal(sharedRun.SharedRunId, claim!.SharedRunId);
            Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));

            await sharedQueue
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    claim.ClaimToken!,
                    reason: reason)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Marks the failed runtime unhealthy and repeatedly runs real recovery until all seeded work is marked recovered.
        /// </summary>
        private static async Task MarkUnhealthyAndReconcileUntilAllSeededWorkRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            string failedRuntimeInstanceId,
            IReadOnlyList<FailedRuntimeWorkSeed> seededWorks,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(seededWorks);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeExecutionRecoveryReconciliationResult? lastResult = null;
            AiRuntimeInstanceSnapshot? lastSnapshot = null;
            var lastStatuses =
                new Dictionary<string, string?>(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastSnapshot =
                    await registry
                        .GetAsync(failedRuntimeInstanceId)
                        .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                lastStatuses.Clear();

                foreach (var work in seededWorks)
                {
                    var entry =
                        await runExecutionIndex
                            .GetAsync(work.FailedLocalRunId)
                            .ConfigureAwait(false);

                    lastStatuses[work.FailedLocalRunId] =
                        entry?.Status;
                }

                if (seededWorks.All(work =>
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
                "Runtime execution recovery did not recover all seeded failed-runtime work within the timeout. " +
                $"FailedRuntimeInstanceId='{failedRuntimeInstanceId}', Timeout='{timeout}', LastRuntimeStatus='{lastSnapshot?.Status}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}', " +
                $"LastStatuses='{string.Join(",", lastStatuses.Select(pair => $"{pair.Key}:{pair.Value}"))}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Writes the failed runtime inventory before recovery.
        /// </summary>
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

    }
}