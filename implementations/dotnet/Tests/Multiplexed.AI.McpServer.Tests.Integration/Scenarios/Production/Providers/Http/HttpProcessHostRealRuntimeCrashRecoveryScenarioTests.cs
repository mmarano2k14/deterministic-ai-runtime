using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Scenarios;
using Multiplexed.AI.Stores;
using System;
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
        private const int KillAfterCompletedStepCount = 50;
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

            Assert.Equal(
                executionId,
                recoveredExecutionId);

            this.output.WriteLine(
                "[REAL RUNTIME CRASH PROOF] Strict DAG resume validated. Runtime process crash recovered on a replacement runtime while preserving the original durable execution id. " +
                $"OriginalExecutionId='{executionId}', RecoveredExecutionId='{recoveredExecutionId}', OriginalRuntimeInstanceId='{failedRuntimeInstanceId}', ReplacementRuntimeInstanceId='{redispatchedRun.AssignedRuntimeInstanceId}'.");

            await ProductionRecoveryWaitHelpers
                .WaitForDagCompletedStepCountAsync(
                    dagStore,
                    executionId,
                    StepCount,
                    TimeSpan.FromMinutes(1))
                .ConfigureAwait(false);

            this.output.WriteLine(
                $"[REAL RUNTIME CRASH PROOF] Recovered redispatch DAG execution completed all durable steps. RecoveredExecutionId='{recoveredExecutionId}', CompletedSteps='{StepCount}'.");
        }
    }
}