using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Health;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
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

            AssertRecoveryOptionsEnabled(recoveryOptions);

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
                await SubmitOneRunAsync(
                        mcp,
                        tenant,
                        pipelineName)
                    .ConfigureAwait(false);

            await WaitForAnyTenantScaleOutRequestFulfilledAsync(
                    scaleOutRequestStore,
                    controlPlaneId,
                    tenant,
                    pipelineName,
                    scenario.ScaleOutTimeout)
                .ConfigureAwait(false);

            var firstDispatch =
                await WaitForSingleDispatchedRunAsync(
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

            await SeedInFlightRuntimeExecutionAsync(
                    sharedRunStore,
                    sharedQueue,
                    runExecutionIndex,
                    firstDispatch,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    existingExecutionId)
                .ConfigureAwait(false);

            await SeedDurableDagStoppedAtStepAsync(
                    dagStore,
                    existingExecutionId,
                    pipelineName,
                    firstDispatch.RunRequest?.PipelineDefinition,
                    StepCount,
                    FailureStepNumber,
                    failedRuntimeInstanceId)
                .ConfigureAwait(false);

            var beforeRecovery =
                await dagStore
                    .GetStateAsync(existingExecutionId)
                    .ConfigureAwait(false);

            AssertDagStoppedAtFailurePoint(
                beforeRecovery,
                FailureStepNumber);

            this.output.WriteLine(
                $"[HTTP DAG RESUME] Seeded DAG state. ExecutionId='{existingExecutionId}', CompletedBeforeFailure='{FailureStepNumber - 1}', FailedStep='{FormatStepName(FailureStepNumber)}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}'.");

            var recoveryResult =
                await MarkUnhealthyAndReconcileUntilRecoveredAsync(
                        registry,
                        healthReconciler,
                        recoveryReconciler,
                        failedRuntimeInstanceId,
                        TimeSpan.FromSeconds(30))
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
            Assert.Equal(AiSharedQueueItemStatus.Pending, queueItemAfterRecovery!.Status);
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

            AssertDagStoppedAtFailurePoint(
                recoveredBeforeRedispatch,
                FailureStepNumber);

            var redispatchedRun =
                await WaitForSharedRunAssignedAwayFromRuntimeAsync(
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
                    $"DagSummary='{FormatDagStateSummary(failedDagState)}'.");
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

            AssertDagCompletedFromFailurePoint(
                finalDagState,
                FailureStepNumber,
                StepCount);

            var replacementIndex =
                await runExecutionIndex
                    .GetAsync(redispatchedRun.LocalRunId!)
                    .ConfigureAwait(false);

            Assert.NotNull(replacementIndex);
            Assert.Equal(existingExecutionId, replacementIndex!.ExecutionId);
            Assert.Equal(redispatchedRun.AssignedRuntimeInstanceId, replacementIndex.RuntimeInstanceId);
            Assert.Equal("completed", replacementIndex.Status);

            this.output.WriteLine(
                $"[HTTP DAG RESUME PROOF] ExecutionId='{existingExecutionId}', FailureStep='{FormatStepName(FailureStepNumber)}', CompletedBeforeFailure='{FailureStepNumber - 1}', RecoveredFromStep='{FormatStepName(FailureStepNumber)}', FinalCompletedSteps='{StepCount}/{StepCount}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', FailedLocalRunId='{failedLocalRunId}', ReplacementLocalRunId='{redispatchedRun.LocalRunId}'.");
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
                            DelayMs = 10,
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
        /// Verifies that runtime execution recovery options are enabled for DAG resume.
        /// </summary>
        /// <param name="options">The recovery options.</param>
        private static void AssertRecoveryOptionsEnabled(
            AiRuntimeExecutionRecoveryReconciliationOptions options)
        {
            Assert.True(options.Enabled, "Runtime execution recovery must be enabled for this test.");
            Assert.True(options.IncludeUnhealthyRuntimeInstances, "Runtime execution recovery must scan unhealthy runtime instances.");
            Assert.True(options.IncludeStoppedRuntimeInstances, "Runtime execution recovery must scan stopped runtime instances.");
            Assert.True(options.IncludeDrainingRuntimeInstances, "Runtime execution recovery must scan draining runtime instances.");
            Assert.True(options.RequeueUnfinishedRuns, "Runtime execution recovery must requeue unfinished runs.");
            Assert.True(options.EnableDagExecutionResume, "DAG resume recovery must be enabled for this test.");
            Assert.False(options.DryRun, "Runtime execution recovery must not run in dry-run mode for this test.");
        }

        /// <summary>
        /// Submits one shared run through the tenant-scoped MCP client.
        /// </summary>
        private static async Task<string> SubmitOneRunAsync(
            McpTestClient mcp,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName)
        {
            var input =
                new Dictionary<string, object?>(
                    tenant.Run.Input,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["delayMs"] = tenant.Run.DelayMs,
                    ["stepCount"] = tenant.Run.StepCount
                };

            var submitRequest =
                new AiSharedRuntimeControllerRequest
                {
                    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                    PipelineKey = pipelineName,
                    TenantId = tenant.TenantId,
                    RequestedBy = RequestedBy,
                    Source = Source,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = tenant.TenantId,
                        [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = tenant.TenantGroupId,
                        ["pipelineName"] = pipelineName,
                        ["runtimeInstanceIdPrefix"] = tenant.RuntimeInstanceIdPrefix
                    },
                    RunRequest = McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: tenant.Run.StepCount,
                        input: input,
                        enableRetention: tenant.Run.EnableRetention,
                        flakyStepInterval: tenant.Run.FlakyStepInterval)
                };

            var submitResults =
                await mcp
                    .SubmitManyRunsAsync(
                        submitRequest,
                        count: 1)
                    .ConfigureAwait(false);

            var submitResult =
                Assert.Single(submitResults);

            Assert.True(
                submitResult.Success,
                submitResult.FailureReason ?? submitResult.Message);

            return ExtractSharedRunId(submitResult);
        }

        /// <summary>
        /// Seeds durable shared queue ownership and runtime execution index for recovery.
        /// </summary>
        private static async Task SeedInFlightRuntimeExecutionAsync(
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            AiSharedRunRecord sharedRun,
            string runtimeInstanceId,
            string localRunId,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(runExecutionIndex);
            ArgumentNullException.ThrowIfNull(sharedRun);

            await sharedRunStore
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    runtimeInstanceId,
                    localRunId,
                    executionId,
                    reason: "http-dag-resume-recovery-seed")
                .ConfigureAwait(false);

            var queueItem =
                await sharedQueue
                    .GetAsync(sharedRun.SharedRunId)
                    .ConfigureAwait(false);

            if (queueItem is null)
            {
                await sharedQueue
                    .EnqueueAsync(new AiSharedQueueItem
                    {
                        SharedRunId = sharedRun.SharedRunId,
                        Status = AiSharedQueueItemStatus.Pending,
                        ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                        PipelineKey = sharedRun.PipelineKey,
                        Priority = 0,
                        EnqueuedAtUtc = DateTimeOffset.UtcNow,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, string>
                        {
                            ["scenario"] = "http-dag-resume-recovery",
                            ["seeded"] = "true"
                        }
                    })
                    .ConfigureAwait(false);

                queueItem =
                    await sharedQueue
                        .GetAsync(sharedRun.SharedRunId)
                        .ConfigureAwait(false);
            }

            Assert.NotNull(queueItem);

            if (queueItem!.Status != AiSharedQueueItemStatus.Dispatched ||
                string.IsNullOrWhiteSpace(queueItem.ClaimToken))
            {
                var claim =
                    await sharedQueue
                        .ClaimNextAsync(new AiSharedQueueClaimRequest
                        {
                            RuntimeInstanceId = runtimeInstanceId,
                            WorkerId = "http-dag-resume-recovery-seed-worker",
                            TenantId = sharedRun.ExecutionContextSnapshot?.TenantId,
                            PipelineKey = sharedRun.PipelineKey,
                            ClaimTtl = TimeSpan.FromMinutes(5),
                            Reason = "http-dag-resume-recovery-seed-claim"
                        })
                        .ConfigureAwait(false);

                Assert.NotNull(claim);
                Assert.Equal(sharedRun.SharedRunId, claim!.SharedRunId);
                Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));

                await sharedQueue
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        claim.ClaimToken!,
                        reason: "http-dag-resume-recovery-seed-dispatch")
                    .ConfigureAwait(false);
            }

            await runExecutionIndex
                .RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = localRunId,
                    ExecutionId = executionId,
                    RuntimeInstanceId = runtimeInstanceId,
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot = sharedRun.ExecutionContextSnapshot,
                    Metadata = new Dictionary<string, string>
                    {
                        ["scenario"] = "http-dag-resume-recovery",
                        ["seeded"] = "true"
                    }
                })
                .ConfigureAwait(false);

            await runExecutionIndex
                .MarkStartedAsync(
                    localRunId,
                    executionId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Seeds a durable DAG state that has completed all steps before the failure point
        /// and has the failure step running with an expired lease.
        /// </summary>
        private static async Task SeedDurableDagStoppedAtStepAsync(
            IAiDagExecutionStore dagStore,
            string executionId,
            string pipelineName,
            AiPipelineDefinition? definition,
            int stepCount,
            int failureStepNumber,
            string failedRuntimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var stepNames =
                ResolveStepNames(
                    definition,
                    stepCount);

            Assert.Equal(stepCount, stepNames.Count);

            var record =
                new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = pipelineName,
                    ExecutionMode = AiExecutionMode.Dag,
                    Status = AiExecutionStatus.Running,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                };

            for (var stepNumber = 1; stepNumber < failureStepNumber; stepNumber++)
            {
                record.CompletedSteps.Add(stepNames[stepNumber - 1]);
            }

            var state =
                new AiExecutionState
                {
                    ExecutionId = executionId,
                    PipelineName = pipelineName
                };

            for (var stepNumber = 1; stepNumber <= stepCount; stepNumber++)
            {
                var stepName =
                    stepNames[stepNumber - 1];

                var dependsOn =
                    ResolveStepDependencies(
                        definition,
                        stepName,
                        stepNumber);

                var step =
                    new AiStepState
                    {
                        StepName = stepName,
                        DependsOn = dependsOn,
                        ClaimTimeoutSeconds = 30,
                        Inputs = new Dictionary<string, object?>(StringComparer.Ordinal),
                        Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                    };

                if (stepNumber < failureStepNumber)
                {
                    step.Status = AiStepExecutionStatus.Completed;
                    step.StartedAtUtc = DateTime.UtcNow.AddMinutes(-5);
                    step.CompletedAtUtc = DateTime.UtcNow.AddMinutes(-4);
                }
                else if (stepNumber == failureStepNumber)
                {
                    step.Status = AiStepExecutionStatus.Running;
                    step.ClaimedBy = $"{failedRuntimeInstanceId}:worker-old";
                    step.ClaimToken = $"claim-token-{Guid.NewGuid():N}";
                    step.ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10);
                    step.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-9);
                    step.RecoveryCount = 0;
                }
                else
                {
                    step.Status = AiStepExecutionStatus.Ready;
                }

                state.Steps[stepName] = step;
            }

            await dagStore
                .CreateAsync(
                    record,
                    state)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Resolves step names from the runtime-generated pipeline definition.
        /// </summary>
        private static IReadOnlyList<string> ResolveStepNames(
            AiPipelineDefinition? definition,
            int stepCount)
        {
            if (definition is not null &&
                definition.Steps.Count == stepCount)
            {
                return definition.Steps
                    .OrderBy(step => step.Order)
                    .Select(step => step.Name)
                    .ToArray();
            }

            return Enumerable
                .Range(1, stepCount)
                .Select(FormatStepName)
                .ToArray();
        }

        /// <summary>
        /// Resolves step dependencies from the generated definition, falling back to a linear DAG.
        /// </summary>
        private static List<string> ResolveStepDependencies(
            AiPipelineDefinition? definition,
            string stepName,
            int stepNumber)
        {
            var definitionStep =
                definition?.Steps.FirstOrDefault(step =>
                    string.Equals(step.Name, stepName, StringComparison.Ordinal));

            if (definitionStep is not null)
            {
                return definitionStep.DependsOn.ToList();
            }

            if (stepNumber == 1)
            {
                return new List<string>();
            }

            return new List<string>
            {
                FormatStepName(stepNumber - 1)
            };
        }

        /// <summary>
        /// Asserts the seeded DAG state before redispatch.
        /// </summary>
        private static void AssertDagStoppedAtFailurePoint(
            AiExecutionState? state,
            int failureStepNumber)
        {
            Assert.NotNull(state);

            var ordered =
                state!.Steps.Values
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(StepCount, ordered.Length);

            for (var index = 0; index < failureStepNumber - 1; index++)
            {
                Assert.Equal(AiStepExecutionStatus.Completed, ordered[index].Status);
                Assert.Equal(0, ordered[index].RecoveryCount);
            }

            var failedStep =
                ordered[failureStepNumber - 1];

            Assert.Equal(AiStepExecutionStatus.Running, failedStep.Status);
            Assert.False(string.IsNullOrWhiteSpace(failedStep.ClaimToken));
            Assert.NotNull(failedStep.LeaseExpiresAtUtc);
            Assert.True(failedStep.LeaseExpiresAtUtc < DateTime.UtcNow);
            Assert.Equal(0, failedStep.RecoveryCount);
        }

        /// <summary>
        /// Asserts the final DAG state after resume.
        /// </summary>
        private static void AssertDagCompletedFromFailurePoint(
            AiExecutionState? state,
            int failureStepNumber,
            int stepCount)
        {
            Assert.NotNull(state);

            var ordered =
                state!.Steps.Values
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(stepCount, ordered.Length);
            Assert.All(ordered, step => Assert.Equal(AiStepExecutionStatus.Completed, step.Status));

            for (var index = 0; index < failureStepNumber - 1; index++)
            {
                Assert.Equal(0, ordered[index].RecoveryCount);
            }

            Assert.True(
                ordered[failureStepNumber - 1].RecoveryCount >= 1,
                $"Expected failure step '{ordered[failureStepNumber - 1].StepName}' to be recovered before resume.");
        }

        /// <summary>
        /// Formats a compact DAG state summary for failed resume diagnostics.
        /// </summary>
        /// <param name="state">The DAG state.</param>
        /// <returns>The formatted DAG state summary.</returns>
        private static string FormatDagStateSummary(
            AiExecutionState? state)
        {
            if (state is null)
            {
                return "<null>";
            }

            var grouped =
                state.Steps.Values
                    .GroupBy(step => step.Status)
                    .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                    .Select(group => $"{group.Key}={group.Count()}");

            var nonCompleted =
                state.Steps.Values
                    .Where(step => step.Status != AiStepExecutionStatus.Completed)
                    .OrderBy(step => step.StepName, StringComparer.Ordinal)
                    .Take(20)
                    .Select(step =>
                        $"{step.StepName}:{step.Status}:Recovery={step.RecoveryCount}:ClaimedBy={step.ClaimedBy ?? string.Empty}:Lease={step.LeaseExpiresAtUtc?.ToString("O") ?? string.Empty}:Error={step.Error ?? string.Empty}");

            return
                $"ExecutionId='{state.ExecutionId}', PipelineName='{state.PipelineName}', " +
                $"Counts='{string.Join(",", grouped)}', " +
                $"NonCompleted='{string.Join(" | ", nonCompleted)}'";
        }

        /// <summary>
        /// Repeatedly marks a runtime unhealthy, reconciles routing health, and runs execution recovery until one in-flight run is recovered.
        /// </summary>
        private static async Task<AiRuntimeExecutionRecoveryReconciliationResult> MarkUnhealthyAndReconcileUntilRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeExecutionRecoveryReconciliationResult? lastResult = null;
            AiRuntimeInstanceSnapshot? lastSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastSnapshot =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                lastResult =
                    await recoveryReconciler
                        .ReconcileAsync()
                        .ConfigureAwait(false);

                if (lastResult.RecoveredRunCount == 1)
                {
                    return lastResult;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Runtime execution recovery did not recover the in-flight run within '{timeout}'. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', LastRuntimeStatus='{lastSnapshot?.Status}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits for the submitted run to become dispatched.
        /// </summary>
        private static async Task<AiSharedRunRecord> WaitForSingleDispatchedRunAsync(
            McpTestClient mcp,
            string pipelineName,
            string sharedRunId,
            TimeSpan timeout)
        {
            var dispatchedRuns =
                await McpTestWaitHelpers
                    .WaitForDispatchedRunsAsync(
                        mcp,
                        pipelineName,
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            sharedRunId
                        },
                        expectedCount: 1,
                        timeout: timeout)
                    .ConfigureAwait(false);

            return Assert.Single(dispatchedRuns);
        }

        /// <summary>
        /// Waits until the shared run is assigned to a runtime different from the failed runtime.
        /// </summary>
        private static async Task<AiSharedRunRecord> WaitForSharedRunAssignedAwayFromRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiSharedRunStore sharedRunStore,
            string sharedRunId,
            string failedRuntimeInstanceId,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiSharedRunRecord? lastRecord = null;
            AiRuntimeInstanceSnapshot? lastFailedRuntimeSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                await registry
                    .MarkUnhealthyAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

                await healthReconciler
                    .ReconcileAsync()
                    .ConfigureAwait(false);

                lastFailedRuntimeSnapshot =
                    await registry
                        .GetAsync(failedRuntimeInstanceId)
                        .ConfigureAwait(false);

                lastRecord =
                    await sharedRunStore
                        .GetAsync(sharedRunId)
                        .ConfigureAwait(false);

                if (lastRecord is not null &&
                    !string.IsNullOrWhiteSpace(lastRecord.AssignedRuntimeInstanceId) &&
                    !string.Equals(
                        lastRecord.AssignedRuntimeInstanceId,
                        failedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    return lastRecord;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Shared run was not redispatched away from failed runtime within '{timeout}'. " +
                $"SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"LastFailedRuntimeStatus='{lastFailedRuntimeSnapshot?.Status}', " +
                $"LastAssignedRuntimeInstanceId='{lastRecord?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRecord?.LocalRunId}', LastExecutionId='{lastRecord?.ExecutionId}'.");

            return lastRecord!;
        }

        /// <summary>
        /// Waits until at least one tenant scale-out request is fulfilled.
        /// </summary>
        private static async Task WaitForAnyTenantScaleOutRequestFulfilledAsync(
            IAiRuntimeScaleOutRequestStore store,
            string controlPlaneId,
            ProductionTenantScenarioDefinition tenant,
            string pipelineName,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> lastRequests =
                Array.Empty<AiRuntimeScaleOutRequestRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastRequests =
                    await store
                        .ListAsync(
                            new AiRuntimeScaleOutRequestQuery
                            {
                                ControlPlaneId = controlPlaneId,
                                TenantId = tenant.TenantId,
                                PipelineKey = pipelineName,
                                MaxResults = 100
                            })
                        .ConfigureAwait(false);

                if (lastRequests.Any(request =>
                        request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled &&
                        !string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId)))
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"No fulfilled scale-out request was observed within '{timeout}'. " +
                $"ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', PipelineKey='{pipelineName}', " +
                $"ObservedRequests='{lastRequests.Count}'.");
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        private static string ExtractSharedRunId(
            object submitResult)
        {
            var resultType =
                submitResult.GetType();

            var directSharedRunId =
                resultType.GetProperty("SharedRunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(directSharedRunId))
            {
                return directSharedRunId;
            }

            var runId =
                resultType.GetProperty("RunId")?.GetValue(submitResult) as string;

            if (!string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            var sharedRun =
                resultType.GetProperty("SharedRun")?.GetValue(submitResult);

            if (sharedRun is not null)
            {
                var sharedRunId =
                    sharedRun
                        .GetType()
                        .GetProperty("SharedRunId")
                        ?.GetValue(sharedRun) as string;

                if (!string.IsNullOrWhiteSpace(sharedRunId))
                {
                    return sharedRunId;
                }
            }

            throw new InvalidOperationException(
                $"Could not extract SharedRunId from submit result type '{resultType.FullName}'.");
        }

        /// <summary>
        /// Formats a stable fallback step name.
        /// </summary>
        private static string FormatStepName(
            int stepNumber)
        {
            return $"step-{stepNumber:000}";
        }
    }
}
