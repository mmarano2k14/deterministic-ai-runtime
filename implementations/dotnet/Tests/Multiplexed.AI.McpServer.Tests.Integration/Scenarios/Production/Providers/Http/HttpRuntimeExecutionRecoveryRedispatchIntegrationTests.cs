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
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http
{
    /// <summary>
    /// HTTP process-host recovery tests proving that an in-flight execution assigned
    /// to an unhealthy HTTP runtime can be recovered and redispatched to another runtime.
    /// </summary>
    public sealed class HttpRuntimeExecutionRecoveryRedispatchIntegrationTests
    {
        private const string RequestedBy = "http-runtime-recovery-redispatch-test";
        private const string Source = "http-runtime-recovery-redispatch";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpRuntimeExecutionRecoveryRedispatchIntegrationTests"/> class.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        public HttpRuntimeExecutionRecoveryRedispatchIntegrationTests(
            ITestOutputHelper output)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that a shared run already dispatched to a real HTTP runtime process can be
        /// recovered when that runtime is marked unhealthy and then redispatched to a new healthy runtime.
        /// </summary>
        [Fact]
        public async Task Http_ProcessHost_Should_Recover_InFlight_Execution_And_Redispatch_To_Healthy_Runtime()
        {
            var scenario =
                CreateHttpRecoveryScenario();

            scenario.DispatchTimeout = TimeSpan.FromMinutes(3);
            scenario.CompletionTimeout = TimeSpan.FromMinutes(4);

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
                $"[HTTP RECOVERY REDISPATCH] Starting. ControlPlaneId='{controlPlaneId}', PipelineKey='{pipelineName}', RuntimeHostAssemblyPath='{runtimeHostAssemblyPath}'.");

            this.output.WriteLine(
                $"[HTTP RECOVERY REDISPATCH] Recovery options. Enabled='{recoveryOptions.Enabled}', IncludeUnhealthy='{recoveryOptions.IncludeUnhealthyRuntimeInstances}', IncludeStopped='{recoveryOptions.IncludeStoppedRuntimeInstances}', IncludeDraining='{recoveryOptions.IncludeDrainingRuntimeInstances}', RequeueUnfinishedRuns='{recoveryOptions.RequeueUnfinishedRuns}', EnableDagExecutionResume='{recoveryOptions.EnableDagExecutionResume}', DryRun='{recoveryOptions.DryRun}'.");

            var sharedRunId =
                await SubmitOneRunAsync(
                        mcp,
                        tenant,
                        pipelineName)
                    .ConfigureAwait(false);

            var initialRuntimeInstanceId =
                await WaitForAnyTenantScaleOutRequestFulfilledAsync(
                        scaleOutRequestStore,
                        controlPlaneId,
                        tenant,
                        pipelineName,
                        scenario.ScaleOutTimeout)
                    .ConfigureAwait(false);

            await WaitForRuntimeInstanceAcceptingRunsAsync(
                    registry,
                    initialRuntimeInstanceId,
                    TimeSpan.FromSeconds(30))
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

            var failedExecutionId =
                $"http-recovery-seeded-execution-{Guid.NewGuid():N}";

            await SeedInFlightRuntimeExecutionAsync(
                    sharedRunStore,
                    sharedQueue,
                    runExecutionIndex,
                    firstDispatch,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    failedExecutionId)
                .ConfigureAwait(false);

            var failedIndexBeforeRecovery =
                await runExecutionIndex
                    .GetAsync(failedLocalRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(failedIndexBeforeRecovery);
            Assert.Equal(failedRuntimeInstanceId, failedIndexBeforeRecovery!.RuntimeInstanceId);
            Assert.Equal(failedExecutionId, failedIndexBeforeRecovery.ExecutionId);
            Assert.Equal("running", failedIndexBeforeRecovery.Status);

            this.output.WriteLine(
                $"[HTTP RECOVERY REDISPATCH] First HTTP dispatch observed and in-flight execution seeded. SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', LocalRunId='{failedLocalRunId}', ExecutionId='{failedExecutionId}'.");

            var recoveryResult =
                await MarkUnhealthyAndReconcileUntilRecoveredAsync(
                        registry,
                        healthReconciler,
                        recoveryReconciler,
                        failedRuntimeInstanceId,
                        TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);

            Assert.True(
                recoveryResult.DiscoveredUnfinishedRunCount >= 1,
                $"Expected at least one discovered unfinished run, actual '{recoveryResult.DiscoveredUnfinishedRunCount}'.");

            Assert.Equal(1, recoveryResult.RecoveredRunCount);

            Assert.Contains(
                recoveryResult.Decisions,
                decision =>
                    decision.RuntimeInstanceId == failedRuntimeInstanceId &&
                    decision.LocalRunId == failedLocalRunId &&
                    decision.ExecutionId == failedExecutionId &&
                    decision.SharedRunId == sharedRunId &&
                    decision.Action == "requeue-shared-run" &&
                    decision.Reason.StartsWith(
                        "transitionReason=runtime-execution-recovery-requeue",
                        StringComparison.Ordinal) &&
                    decision.Changed);

            var failedIndexAfterRecovery =
                await runExecutionIndex
                    .GetAsync(failedLocalRunId)
                    .ConfigureAwait(false);

            Assert.NotNull(failedIndexAfterRecovery);
            Assert.Equal("requeued-for-recovery", failedIndexAfterRecovery!.Status);
            Assert.Equal("runtime-execution-recovery-requeue", failedIndexAfterRecovery.FailureReason);
            Assert.NotNull(failedIndexAfterRecovery.CompletedAtUtc);

            var failedRuntimeUnfinishedRuns =
                await runExecutionIndex
                    .ListUnfinishedByRuntimeInstanceAsync(failedRuntimeInstanceId)
                    .ConfigureAwait(false);

            Assert.Empty(failedRuntimeUnfinishedRuns);

            this.output.WriteLine(
                $"[HTTP RECOVERY REDISPATCH] Recovery completed. SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}'.");

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

            this.output.WriteLine(
                $"[HTTP RECOVERY REDISPATCH] Redispatch observed. SharedRunId='{sharedRunId}', NewRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}', NewLocalRunId='{redispatchedRun.LocalRunId}', NewExecutionId='{redispatchedRun.ExecutionId}'.");

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
            Assert.Equal("completed", finalStatus.RunState?.Status);

            var recoveredExecutionId =
                finalStatus.ExecutionId ??
                finalStatus.RunState?.ExecutionId ??
                redispatchedRun.ExecutionId;

            Assert.False(string.IsNullOrWhiteSpace(recoveredExecutionId));
            Assert.NotEqual(failedExecutionId, recoveredExecutionId);

            this.output.WriteLine(
                $"[HTTP RECOVERY REDISPATCH] Completed. SharedRunId='{sharedRunId}', FailedExecutionId='{failedExecutionId}', RecoveredExecutionId='{recoveredExecutionId}'.");
        }

        /// <summary>
        /// Creates a focused HTTP process-host recovery scenario.
        /// </summary>
        /// <returns>The scenario definition.</returns>
        private static ProductionRuntimeScenarioDefinition CreateHttpRecoveryScenario()
        {
            var baseScenario =
                ProductionRuntimeScenarioFactory.CreateSingleTenantDedicatedRuntimeModeScenario();

            var tenant =
                baseScenario.Tenants.Single();

            return baseScenario with
            {
                Name = "http-process-host-inflight-recovery-redispatch",
                ControlPlaneIdPrefix = "http-process-host-inflight-recovery-redispatch",
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
                            StepCount = 20,
                            DelayMs = 250,
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
                CompletionTimeout = TimeSpan.FromMinutes(4),
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
        /// Verifies that runtime execution recovery is enabled for this host.
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
            Assert.False(options.EnableDagExecutionResume, "DAG resume must be disabled for the legacy redispatch recovery test because it expects a new recovered execution id.");
            Assert.False(options.DryRun, "Runtime execution recovery must not run in dry-run mode for this test.");
        }

        /// <summary>
        /// Submits one shared run through the tenant-scoped MCP client.
        /// </summary>
        /// <param name="mcp">The tenant-scoped MCP client.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <returns>The submitted shared run id.</returns>
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
        /// Seeds the durable shared queue ownership and runtime execution index for the HTTP recovery scenario.
        /// </summary>
        /// <remarks>
        /// The HTTP process-host scenario proves real HTTP runtime dispatch, but the spawned
        /// runtime process does not yet publish its local execution index into the control-plane
        /// test service provider. This helper simulates the durable in-flight execution
        /// observation and shared queue dispatched ownership required by runtime execution recovery.
        /// </remarks>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedQueue">The shared queue.</param>
        /// <param name="runExecutionIndex">The runtime run execution index.</param>
        /// <param name="sharedRun">The dispatched shared run.</param>
        /// <param name="runtimeInstanceId">The failed runtime instance id.</param>
        /// <param name="localRunId">The failed runtime local run id.</param>
        /// <param name="executionId">The seeded execution id.</param>
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
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(localRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            await sharedRunStore
                .MarkDispatchedAsync(
                    sharedRun.SharedRunId,
                    runtimeInstanceId,
                    localRunId,
                    executionId,
                    reason: "http-runtime-inflight-recovery-seed")
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
                            ["scenario"] = "http-runtime-inflight-recovery-redispatch",
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
                            WorkerId = "http-runtime-inflight-recovery-seed-worker",
                            TenantId = sharedRun.ExecutionContextSnapshot?.TenantId,
                            PipelineKey = sharedRun.PipelineKey,
                            ClaimTtl = TimeSpan.FromMinutes(5),
                            Reason = "http-runtime-inflight-recovery-seed-claim"
                        })
                        .ConfigureAwait(false);

                Assert.NotNull(claim);
                Assert.Equal(sharedRun.SharedRunId, claim!.SharedRunId);
                Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));

                await sharedQueue
                    .MarkDispatchedAsync(
                        sharedRun.SharedRunId,
                        claim.ClaimToken!,
                        reason: "http-runtime-inflight-recovery-seed-dispatch")
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
                        ["scenario"] = "http-runtime-inflight-recovery-redispatch",
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
        /// Repeatedly marks a runtime unhealthy, reconciles routing health, and runs execution recovery until one in-flight run is recovered.
        /// </summary>
        /// <remarks>
        /// Runtime execution recovery and runtime instance health reconciliation are intentionally separate.
        /// This helper runs both in the test: health reconciliation prevents the failed runtime from being
        /// selected again, while execution recovery requeues the already-dispatched in-flight run.
        /// </remarks>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="recoveryReconciler">The runtime execution recovery reconciler.</param>
        /// <param name="runtimeInstanceId">The runtime instance id to mark unhealthy.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The recovery reconciliation result that recovered the in-flight run.</returns>
        private static async Task<AiRuntimeExecutionRecoveryReconciliationResult> MarkUnhealthyAndReconcileUntilRecoveredAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

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
                $"RuntimeInstanceId='{runtimeInstanceId}', " +
                $"LastRuntimeStatus='{lastSnapshot?.Status}', LastCanAcceptRun='{lastSnapshot?.CanAcceptRun}', " +
                $"LastScannedRuntimeInstanceCount='{lastResult?.ScannedRuntimeInstanceCount}', " +
                $"LastIgnoredRuntimeInstanceCount='{lastResult?.IgnoredRuntimeInstanceCount}', " +
                $"LastDiscoveredUnfinishedRunCount='{lastResult?.DiscoveredUnfinishedRunCount}', " +
                $"LastRecoveredRunCount='{lastResult?.RecoveredRunCount}'." +
                Environment.NewLine +
                FormatRecoveryDecisions(lastResult));

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Formats recovery decisions for assertion diagnostics.
        /// </summary>
        /// <param name="result">The recovery reconciliation result.</param>
        /// <returns>The formatted recovery decisions.</returns>
        private static string FormatRecoveryDecisions(
            AiRuntimeExecutionRecoveryReconciliationResult? result)
        {
            if (result is null)
            {
                return "Recovery decisions: <null result>";
            }

            if (result.Decisions.Count == 0)
            {
                return "Recovery decisions: <empty>";
            }

            return "Recovery decisions:" +
                Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    result.Decisions.Select(decision =>
                        $"RuntimeInstanceId='{decision.RuntimeInstanceId}', " +
                        $"LocalRunId='{decision.LocalRunId}', " +
                        $"ExecutionId='{decision.ExecutionId}', " +
                        $"SharedRunId='{decision.SharedRunId}', " +
                        $"TenantId='{decision.TenantId}', " +
                        $"TenantGroupId='{decision.TenantGroupId}', " +
                        $"Action='{decision.Action}', " +
                        $"Reason='{decision.Reason}', " +
                        $"Changed='{decision.Changed}'."));
        }

        /// <summary>
        /// Waits for the submitted run to become dispatched.
        /// </summary>
        /// <param name="mcp">The MCP test client.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The dispatched shared run record.</returns>
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
        /// <remarks>
        /// In the HTTP process-host scenario the failed runtime process can still emit heartbeats.
        /// While waiting for redispatch, the test continuously keeps the failed runtime unhealthy
        /// and reruns health reconciliation so the dispatcher does not select it again.
        /// </remarks>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="healthReconciler">The runtime instance health reconciler.</param>
        /// <param name="sharedRunStore">The shared run store.</param>
        /// <param name="sharedRunId">The shared run id.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance id.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>The redispatched shared run record.</returns>
        private static async Task<AiSharedRunRecord> WaitForSharedRunAssignedAwayFromRuntimeAsync(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceHealthReconciler healthReconciler,
            IAiSharedRunStore sharedRunStore,
            string sharedRunId,
            string failedRuntimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(healthReconciler);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);

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
                $"Shared run was not redispatched away from failed runtime within 2 '{timeout}'. " +
                $"SharedRunId='{sharedRunId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', " +
                $"LastFailedRuntimeStatus='{lastFailedRuntimeSnapshot?.Status}', " +
                $"LastFailedRuntimeCanAcceptRun='{lastFailedRuntimeSnapshot?.CanAcceptRun}', " +
                $"LastAssignedRuntimeInstanceId='{lastRecord?.AssignedRuntimeInstanceId}', " +
                $"LastLocalRunId='{lastRecord?.LocalRunId}', LastExecutionId='{lastRecord?.ExecutionId}'.");

            return lastRecord!;
        }

        /// <summary>
        /// Waits until at least one tenant scale-out request is fulfilled.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenant">The tenant scenario definition.</param>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="timeout">The timeout.</param>
        private static async Task<string> WaitForAnyTenantScaleOutRequestFulfilledAsync(
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

                var fulfilledRequest =
                    lastRequests.FirstOrDefault(request =>
                        request.Status == AiRuntimeScaleOutRequestStatus.Fulfilled &&
                        !string.IsNullOrWhiteSpace(request.FulfilledRuntimeInstanceId));

                if (fulfilledRequest is not null)
                {
                    return fulfilledRequest.FulfilledRuntimeInstanceId!;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"No fulfilled scale-out request was observed within '{timeout}'. " +
                $"ControlPlaneId='{controlPlaneId}', TenantId='{tenant.TenantId}', PipelineKey='{pipelineName}', " +
                $"ObservedRequests='{lastRequests.Count}'.");

            throw new InvalidOperationException(
                "Unreachable assertion path.");
        }

        /// <summary>
        /// Waits until the fulfilled runtime instance is visible and can accept runs.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="timeout">The timeout.</param>
        private static async Task WaitForRuntimeInstanceAcceptingRunsAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            AiRuntimeInstanceSnapshot? lastSnapshot = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastSnapshot =
                    await registry
                        .GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                if (lastSnapshot is not null &&
                    lastSnapshot.CanAcceptRun &&
                    lastSnapshot.Status != AiRuntimeInstanceStatus.Unhealthy &&
                    lastSnapshot.Status != AiRuntimeInstanceStatus.Stopped)
                {
                    return;
                }

                await Task
                    .Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            Assert.Fail(
                $"Fulfilled runtime instance was not ready to accept runs within '{timeout}'. " +
                $"RuntimeInstanceId='{runtimeInstanceId}', " +
                $"LastStatus='{lastSnapshot?.Status}', LastCanAcceptRun='{lastSnapshot?.CanAcceptRun}'.");
        }

        /// <summary>
        /// Extracts the shared run id from a submit result.
        /// </summary>
        /// <param name="submitResult">The submit result.</param>
        /// <returns>The shared run id.</returns>
        private static string ExtractSharedRunId(
            object submitResult)
        {
            ArgumentNullException.ThrowIfNull(submitResult);

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
    }
}