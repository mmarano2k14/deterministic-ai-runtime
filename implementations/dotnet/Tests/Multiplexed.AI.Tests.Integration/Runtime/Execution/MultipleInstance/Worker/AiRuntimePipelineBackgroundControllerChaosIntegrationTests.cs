using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker
{
    /// <summary>
    /// End-to-end chaos integration tests for the runtime pipeline background controller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests validate the runtime controller as a higher-level execution control plane:
    /// queued pipeline runs, background execution, retry, retention, compaction, eviction,
    /// concurrency configuration, throttling configuration, worker metrics, tracing,
    /// and strict RunId / ExecutionId separation.
    /// </para>
    /// </remarks>
    [Collection("redis")]
    public sealed class AiRuntimePipelineBackgroundControllerChaosIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePipelineBackgroundControllerChaosIntegrationTests"/> class.
        /// </summary>
        /// <param name="output">The xUnit output helper.</param>
        public AiRuntimePipelineBackgroundControllerChaosIntegrationTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Validates a small but complete runtime controller scenario with retry,
        /// policy-driven retention, compaction/eviction configuration, concurrency config,
        /// tracing, metrics, and strict RunId / ExecutionId isolation.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Run_Small_Validated_Runtime_Simulation()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var timeline = host.ServiceProvider.GetRequiredService<IAiTraceTimeline>();

            await controller.StartAsync();

            var handles = new List<AiRuntimeWorkerRunHandle>();

            try
            {
                handles.AddRange(
                    await EnqueueScenarioRunsAsync(
                        controller,
                        scenario));

                Assert.Equal(
                    scenario.RunCount,
                    handles.Count);

                Assert.All(
                    handles,
                    AssertHandleAcceptedAfterEnqueue);

                var finals = await WaitForCompletionsAsync(
                    handles,
                    scenario.Timeout);

                await AssertScenarioCompletedAsync(
                    scenario,
                    handles,
                    finals,
                    dagStore,
                    resolver);

                AssertWorkerMetricsRecorded(
                    metrics);

                AssertTraceTimelineRecorded(
                    timeline,
                    handles);

                RenderTraceTimeline(
                    timeline,
                    handles);
            }
            finally
            {
                await controller.StopAsync();

                await CleanupExecutionsAsync(
                    host.ServiceProvider,
                    handles);
            }
        }

        /// <summary>
        /// Runs a larger chaos-style runtime simulation with multiple queued runs,
        /// a 50-step DAG, retryable chaos steps, policy-driven retention,
        /// compaction/eviction trigger configuration, concurrency/throttling configuration,
        /// metrics, and tracing.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Run_Chaos_Runtime_Simulation_With_Retry_Retention_Concurrency_Tracing_And_Metrics()
        {
            var scenario = AiRuntimeChaosScenario.Chaos();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var timeline = host.ServiceProvider.GetRequiredService<IAiTraceTimeline>();

            await controller.StartAsync();

            var handles = new List<AiRuntimeWorkerRunHandle>();

            try
            {
                handles.AddRange(
                    await EnqueueScenarioRunsAsync(
                        controller,
                        scenario));

                Assert.Equal(
                    scenario.RunCount,
                    handles.Count);

                Assert.All(
                    handles,
                    AssertHandleAcceptedAfterEnqueue);

                var finals = await WaitForCompletionsAsync(
                    handles,
                    scenario.Timeout);

                await AssertScenarioCompletedAsync(
                    scenario,
                    handles,
                    finals,
                    dagStore,
                    resolver);

                AssertWorkerMetricsRecorded(
                    metrics);

                AssertTraceTimelineRecorded(
                    timeline,
                    handles);

                RenderTraceTimeline(
                    timeline,
                    handles);
            }
            finally
            {
                await controller.StopAsync();

                await CleanupExecutionsAsync(
                    host.ServiceProvider,
                    handles);
            }
        }

        /// <summary>
        /// Verifies that an execution created through the background controller completes,
        /// is finalized naturally, has a snapshot available, remains replay-callable after
        /// completion, and reports AlreadyExists when live record/state still exist.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Report_AlreadyExists_When_Replay_Is_Called_For_Existing_Execution()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-replay-001",
                            source = "background-controller-replay-test"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.RunId),
                    "The controller RunId must be assigned.");

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId),
                    "The background controller must create a runtime ExecutionId.");

                Assert.False(
                    string.Equals(
                        handle.RunId,
                        handle.ExecutionId,
                        StringComparison.Ordinal),
                    "RunId and ExecutionId must be different. RunId belongs to the controller lifecycle; ExecutionId belongs to the runtime execution namespace.");

                Assert.Equal(
                    final.ExecutionId,
                    handle.ExecutionId);

                var executionRecord = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionRecord);
                Assert.Equal(handle.ExecutionId, executionRecord!.ExecutionId);
                Assert.Equal(scenario.PipelineName, executionRecord.PipelineName);
                Assert.Equal(AiExecutionStatus.Completed, executionRecord.Status);
                Assert.True(executionRecord.IsTerminal);

                var executionState = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionState);
                Assert.Equal(handle.ExecutionId, executionState!.ExecutionId);
                Assert.Equal(scenario.PipelineName, executionState.PipelineName);

                var runIdRecord = await dagStore.GetRecordAsync(
                    handle.RunId);

                Assert.Null(runIdRecord);

                var runIdState = await dagStore.GetStateAsync(
                    handle.RunId);

                Assert.Null(runIdState);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    executionState);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    executionState,
                    resolver);

                var snapshot = await WaitForSnapshotAfterFinalizationAsync(
                    host.Engine,
                    snapshotStore,
                    handle.ExecutionId!,
                    TimeSpan.FromSeconds(10));

                Assert.NotNull(snapshot);
                Assert.Equal(handle.ExecutionId, snapshot.ExecutionId);

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = handle.ExecutionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete
                    });

                Assert.NotNull(replayResult);

                _output.WriteLine(
                    $"Replay result for ExecutionId='{handle.ExecutionId}': ReplayValid='{replayResult.ReplayValid}'.");

                Assert.True(
                    replayResult.ReplayValid,
                    "Replay should be valid when the live execution already exists.");

                Assert.True(
                    replayResult.ExecutionFound,
                    "Replay should report ExecutionFound when the live execution exists.");

                Assert.True(
                    replayResult.SnapshotFound,
                    "Replay should report SnapshotFound when the snapshot exists.");

                var recordAfterReplay = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(recordAfterReplay);
                Assert.Equal(handle.ExecutionId, recordAfterReplay!.ExecutionId);
                Assert.Equal(scenario.PipelineName, recordAfterReplay.PipelineName);
                Assert.Equal(AiExecutionStatus.Completed, recordAfterReplay.Status);
                Assert.True(recordAfterReplay.IsTerminal);

                var stateAfterReplay = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(stateAfterReplay);
                Assert.Equal(handle.ExecutionId, stateAfterReplay!.ExecutionId);
                Assert.Equal(scenario.PipelineName, stateAfterReplay.PipelineName);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    stateAfterReplay);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    stateAfterReplay,
                    resolver);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Verifies that a completed background-controller execution can be restored from
        /// its terminal snapshot after the live DAG record/state has been removed.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Restore_Execution_From_Snapshot_After_State_Delete()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-replay-restore-001",
                            source = "background-controller-replay-restore-test"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.RunId),
                    "The controller RunId must be assigned.");

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId),
                    "The background controller must create a runtime ExecutionId.");

                Assert.False(
                    string.Equals(
                        handle.RunId,
                        handle.ExecutionId,
                        StringComparison.Ordinal),
                    "RunId and ExecutionId must be different. RunId belongs to the controller lifecycle; ExecutionId belongs to the runtime execution namespace.");

                Assert.Equal(
                    final.ExecutionId,
                    handle.ExecutionId);

                var executionId = handle.ExecutionId!;

                var snapshot = await WaitForSnapshotAfterFinalizationAsync(
                    host.Engine,
                    snapshotStore,
                    executionId,
                    TimeSpan.FromSeconds(10));

                Assert.NotNull(snapshot);
                Assert.Equal(executionId, snapshot.ExecutionId);

                var recordBeforeDelete = await dagStore.GetRecordAsync(
                    executionId);

                var stateBeforeDelete = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(recordBeforeDelete);
                Assert.NotNull(stateBeforeDelete);
                Assert.Equal(AiExecutionStatus.Completed, recordBeforeDelete!.Status);
                Assert.True(recordBeforeDelete.IsTerminal);
                Assert.Equal(scenario.StepCount, recordBeforeDelete.CompletedSteps.Count);

                await resolver.WarmAsync(
                    executionId,
                    stateBeforeDelete!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    stateBeforeDelete!,
                    resolver);

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                var deletedRecord = await dagStore.GetRecordAsync(
                    executionId);

                var deletedState = await dagStore.GetStateAsync(
                    executionId);

                Assert.Null(deletedRecord);
                Assert.Null(deletedState);

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = handle.ExecutionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete
                    });

                Assert.NotNull(replayResult);

                _output.WriteLine(
                    $"Replay restore result for ExecutionId='{executionId}': ReplayValid='{replayResult.ReplayValid}'.");

                Assert.True(
                    replayResult.ReplayValid,
                    "Replay should restore the execution after the live DAG record/state has been deleted.");

                Assert.True(
                    replayResult.ExecutionFound,
                    "Replay should report ExecutionFound after restoring the execution.");

                Assert.True(
                    replayResult.SnapshotFound,
                    "Replay should report SnapshotFound after restoring the execution.");

                var restoredRecord = await dagStore.GetRecordAsync(
                    executionId);

                var restoredState = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(restoredRecord);
                Assert.NotNull(restoredState);

                Assert.Equal(executionId, restoredRecord!.ExecutionId);
                Assert.Equal(executionId, restoredState!.ExecutionId);

                Assert.Equal(AiExecutionStatus.Completed, restoredRecord.Status);
                Assert.True(restoredRecord.IsTerminal);
                Assert.Equal(scenario.PipelineName, restoredRecord.PipelineName);

                Assert.Equal(
                    scenario.StepCount,
                    restoredRecord.CompletedSteps.Count);

                Assert.Equal(
                    scenario.PipelineName,
                    restoredState.PipelineName);

                await resolver.WarmAsync(
                    executionId,
                    restoredState,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    restoredState,
                    resolver);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Verifies that completed required steps remain resolvable after a normal background
        /// controller execution and terminal lifecycle processing.
        /// </summary>
        /// <remarks>
        /// This test is intentionally diagnostic. It does not modify the batch runner.
        /// Its purpose is to identify whether resolver status becomes None after retention,
        /// snapshot, cleanup, or replay-related lifecycle operations.
        /// </remarks>
        [RedisFact]
        public async Task BackgroundController_Should_Keep_Completed_Steps_Resolvable_After_Terminal_Lifecycle()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-resolver-diagnostic-001",
                            source = "background-controller-resolver-diagnostic"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.Equal(final.ExecutionId, handle.ExecutionId);

                var record = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(record);
                Assert.Equal(AiExecutionStatus.Completed, record!.Status);
                Assert.True(record.IsTerminal);

                var state = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(state);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    state!,
                    CancellationToken.None);

                var unresolved = new List<string>();

                foreach (var step in scenario.PipelineDefinition.Steps)
                {
                    var stepName = step.Name;

                    if (string.IsNullOrWhiteSpace(stepName))
                    {
                        continue;
                    }

                    var resolved = await resolver.GetStepStatusAsync(
                        handle.ExecutionId!,
                        stepName,
                        state!,
                        CancellationToken.None);

                    if (resolved is null ||
                        resolved.Status != AiStepExecutionStatus.Completed)
                    {
                        unresolved.Add(
                            $"{stepName}: {(resolved is null ? "null" : resolved.Status.ToString())}");
                    }
                }

                if (unresolved.Count > 0)
                {
                    _output.WriteLine("Unresolved completed steps after terminal lifecycle:");

                    foreach (var item in unresolved)
                    {
                        _output.WriteLine(item);
                    }
                }

                Assert.True(
                    unresolved.Count == 0,
                    "All completed pipeline steps must remain resolvable as Completed after terminal lifecycle. Unresolved: " +
                    string.Join(", ", unresolved));
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Verifies that a completed background-controller execution restored from snapshot
        /// produces the same deterministic execution result as the original completed execution.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Restore_Execution_From_Snapshot_With_Deterministic_Result()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-replay-deterministic-001",
                            source = "background-controller-replay-deterministic-test"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);

                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);
                Assert.Equal(final.ExecutionId, handle.ExecutionId);

                var executionId = handle.ExecutionId!;

                var snapshot = await WaitForSnapshotAfterFinalizationAsync(
                    host.Engine,
                    snapshotStore,
                    executionId,
                    TimeSpan.FromSeconds(10));

                Assert.NotNull(snapshot);
                Assert.Equal(executionId, snapshot.ExecutionId);

                var recordBeforeDelete = await dagStore.GetRecordAsync(
                    executionId);

                var stateBeforeDelete = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(recordBeforeDelete);
                Assert.NotNull(stateBeforeDelete);

                Assert.Equal(AiExecutionStatus.Completed, recordBeforeDelete!.Status);
                Assert.True(recordBeforeDelete.IsTerminal);
                Assert.Equal(scenario.StepCount, recordBeforeDelete.CompletedSteps.Count);

                await resolver.WarmAsync(
                    executionId,
                    stateBeforeDelete!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    stateBeforeDelete!,
                    resolver);

                var beforeReplayFingerprint = await CreateReplayDeterminismFingerprintAsync(
                    scenario,
                    executionId,
                    recordBeforeDelete,
                    stateBeforeDelete!,
                    resolver);

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                var deletedRecord = await dagStore.GetRecordAsync(
                    executionId);

                var deletedState = await dagStore.GetStateAsync(
                    executionId);

                Assert.Null(deletedRecord);
                Assert.Null(deletedState);

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = handle.ExecutionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete
                    });

                _output.WriteLine(
                    $"Replay result for ExecutionId='{handle.ExecutionId}': ReplayValid='{replayResult.ReplayValid}'.");

                Assert.True(
                    replayResult.ReplayValid,
                    "Replay should be valid when the live execution already exists.");

                Assert.True(
                    replayResult.ExecutionFound,
                    "Replay should report ExecutionFound when the live execution exists.");

                Assert.True(
                    replayResult.SnapshotFound,
                    "Replay should report SnapshotFound when the snapshot exists.");

                var restoredRecord = await dagStore.GetRecordAsync(
                    executionId);

                var restoredState = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(restoredRecord);
                Assert.NotNull(restoredState);

                Assert.Equal(AiExecutionStatus.Completed, restoredRecord!.Status);
                Assert.True(restoredRecord.IsTerminal);
                Assert.Equal(scenario.StepCount, restoredRecord.CompletedSteps.Count);

                await resolver.WarmAsync(
                    executionId,
                    restoredState!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    restoredState!,
                    resolver);

                var afterReplayFingerprint = await CreateReplayDeterminismFingerprintAsync(
                    scenario,
                    executionId,
                    restoredRecord,
                    restoredState!,
                    resolver);

                Assert.Equal(
                    beforeReplayFingerprint.ExecutionId,
                    afterReplayFingerprint.ExecutionId);

                Assert.Equal(
                    beforeReplayFingerprint.PipelineName,
                    afterReplayFingerprint.PipelineName);

                Assert.Equal(
                    beforeReplayFingerprint.Status,
                    afterReplayFingerprint.Status);

                Assert.Equal(
                    beforeReplayFingerprint.IsTerminal,
                    afterReplayFingerprint.IsTerminal);

                Assert.Equal(
                    beforeReplayFingerprint.CompletedSteps,
                    afterReplayFingerprint.CompletedSteps);

                Assert.Equal(
                    beforeReplayFingerprint.StepStatuses,
                    afterReplayFingerprint.StepStatuses);

                Assert.Equal(
                    beforeReplayFingerprint.RetryCounts,
                    afterReplayFingerprint.RetryCounts);

                Assert.Equal(
                    beforeReplayFingerprint.RequiredResolvedSteps,
                    afterReplayFingerprint.RequiredResolvedSteps);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }
        /// <summary>
        /// Verifies that the background controller can execute a single runtime execution
        /// through multiple distributed runtime workers sharing the same execution identifier.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Run_Distributed_Multi_Instance_When_Enabled()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-distributed-001",
                            source = "distributed-runtime-workers"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    final.Status);

                Assert.Equal(
                    scenario.StepCount,
                    final.CompletedSteps.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.RunId));

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId));

                Assert.NotEqual(
                    handle.RunId,
                    handle.ExecutionId);

                Assert.Equal(
                    handle.ExecutionId,
                    final.ExecutionId);

                _output.WriteLine(
                    $"Distributed controller run completed. RunId='{handle.RunId}', HandleExecutionId='{handle.ExecutionId}', FinalExecutionId='{final.ExecutionId}'.");

                var executionRecord = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionRecord);

                Assert.Equal(
                    handle.ExecutionId,
                    executionRecord!.ExecutionId);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    executionRecord.Status);

                Assert.True(
                    executionRecord.IsTerminal);

                var executionState = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionState);

                Assert.Equal(
                    handle.ExecutionId,
                    executionState!.ExecutionId);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    executionState,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    executionState,
                    resolver);

                AssertNoStaleClaims(
                    executionState);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(workerCycles);

                _output.WriteLine(
                    $"Runtime worker cycle metric count='{workerCycles.Count}'.");

                foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"RuntimeInstanceId='{item.Key}', Cycles='{item.Value}'.");
                }

                Assert.True(
                    workerCycles.Values.Sum() > 0,
                    "Expected distributed runtime workers to record worker cycles.");

                Assert.True(
                    workerCycles.Count >= 2,
                    "Expected multiple runtime instance workers to participate in distributed execution. " +
                    "If this fails while ExecutionId is identical, the worker group is sharing one runtime instance identity.");
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Verifies that distributed runtime workers stop cleanly after terminal execution observation.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Stop_Distributed_Workers_After_Terminal_Observation()
        {
            var scenario = AiRuntimeChaosScenario.Small();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-distributed-stop-001",
                            source = "distributed-runtime-workers-stop-terminal"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    final.Status);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId));

                Assert.Equal(
                    handle.ExecutionId,
                    final.ExecutionId);

                Assert.Equal(
                    AiRuntimeWorkerRunStatus.Completed,
                    handle.Status);

                var executionState = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionState);

                AssertNoStaleClaims(
                    executionState!);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(workerCycles);

                Assert.True(
                    workerCycles.Count >= 2,
                    "Expected multiple distributed runtime workers to record cycles before terminal observation.");

                _output.WriteLine(
                    $"Distributed terminal observation completed. ExecutionId='{handle.ExecutionId}', RuntimeWorkers='{workerCycles.Count}'.");

                foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"RuntimeInstanceId='{item.Key}', Cycles='{item.Value}'.");
                }
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Verifies that distributed multi-runtime-instance execution preserves the same
        /// deterministic execution shape as the default single runtime worker path.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Preserve_Deterministic_Result_With_Distributed_Workers()
        {
            var singleScenario = AiRuntimeChaosScenario.Small();
            var distributedScenario = AiRuntimeChaosScenario.Small();

            await using var singleHost = await CreateChaosHostAsync(
                singleScenario);

            await using var distributedHost = await CreateDistributedChaosHostAsync(
                distributedScenario);

            var singleFingerprint = await RunScenarioAndCreateCompletionFingerprintAsync(
                singleHost,
                singleScenario,
                "candidate-single-deterministic-001",
                "single-runtime-worker-deterministic");

            var distributedFingerprint = await RunScenarioAndCreateCompletionFingerprintAsync(
                distributedHost,
                distributedScenario,
                "candidate-distributed-deterministic-001",
                "distributed-runtime-workers-deterministic");

            Assert.Equal(
                singleFingerprint.Status,
                distributedFingerprint.Status);

            Assert.Equal(
                singleFingerprint.IsTerminal,
                distributedFingerprint.IsTerminal);

            Assert.Equal(
                singleFingerprint.CompletedStepCount,
                distributedFingerprint.CompletedStepCount);

            Assert.Equal(
                singleFingerprint.StepStatuses,
                distributedFingerprint.StepStatuses);

            Assert.Equal(
                singleFingerprint.RetryCounts,
                distributedFingerprint.RetryCounts);

            Assert.Equal(
                singleFingerprint.RequiredResolvedSteps,
                distributedFingerprint.RequiredResolvedSteps);
        }

        /// <summary>
        /// Verifies that distributed runtime workers can safely race for step claims
        /// on the same execution identifier without leaving stale ownership metadata.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Handle_Distributed_Claim_Races_Safely()
        {
            var scenario = AiRuntimeChaosScenario.Chaos();

            await using var host = await CreateDistributedClaimRaceHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-distributed-claim-race-001",
                            source = "distributed-runtime-workers-claim-race"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    final.Status);

                Assert.Equal(
                    scenario.StepCount,
                    final.CompletedSteps.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId));

                Assert.Equal(
                    handle.ExecutionId,
                    final.ExecutionId);

                var state = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(state);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    state!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    state!,
                    resolver);

                AssertNoStaleClaims(
                    state!);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(workerCycles);

                Assert.True(
                    workerCycles.Count >= 2,
                    "Expected multiple runtime instance workers to participate in claim-race execution.");

                _output.WriteLine(
                    $"Distributed claim-race execution completed. ExecutionId='{handle.ExecutionId}', RuntimeWorkers='{workerCycles.Count}', CompletedSteps='{final.CompletedSteps.Count}'.");

                foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"RuntimeInstanceId='{item.Key}', Cycles='{item.Value}'.");
                }
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Verifies that distributed multi-runtime-instance execution remains compatible
        /// with retry, retention, snapshot restore, and deterministic replay semantics.
        /// </summary>
        [RedisFact]
        public async Task BackgroundController_Should_Work_With_Distributed_Retry_Retention_And_Replay()
        {
            var scenario = AiRuntimeChaosScenario.Chaos();

            await using var host = await CreateDistributedClaimRaceHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = "candidate-distributed-replay-001",
                            source = "distributed-runtime-workers-replay"
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);

                Assert.True(
                    final.IsTerminal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    final.Status);

                Assert.Equal(
                    scenario.StepCount,
                    final.CompletedSteps.Count);

                Assert.False(
                    string.IsNullOrWhiteSpace(handle.ExecutionId));

                Assert.Equal(
                    handle.ExecutionId,
                    final.ExecutionId);

                var executionId = handle.ExecutionId!;

                var snapshot = await WaitForSnapshotAfterFinalizationAsync(
                    host.Engine,
                    snapshotStore,
                    executionId,
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(snapshot);

                Assert.Equal(
                    executionId,
                    snapshot.ExecutionId);

                var recordBeforeReplay = await dagStore.GetRecordAsync(
                    executionId);

                var stateBeforeReplay = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(recordBeforeReplay);
                Assert.NotNull(stateBeforeReplay);

                await resolver.WarmAsync(
                    executionId,
                    stateBeforeReplay!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    stateBeforeReplay!,
                    resolver);

                AssertNoStaleClaims(
                    stateBeforeReplay!);

                var beforeReplayFingerprint =
                    await CreateReplayDeterminismFingerprintAsync(
                        scenario,
                        executionId,
                        recordBeforeReplay!,
                        stateBeforeReplay!,
                        resolver);

                Assert.NotNull(snapshot);

                Assert.Equal(
                    executionId,
                    snapshot.ExecutionId);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(workerCycles);

                Assert.True(
                    workerCycles.Count >= 2,
                    "Expected multiple distributed runtime workers before replay.");

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                var deletedRecord = await dagStore.GetRecordAsync(
                    executionId);

                var deletedState = await dagStore.GetStateAsync(
                    executionId);

                Assert.Null(deletedRecord);
                Assert.Null(deletedState);

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = handle.ExecutionId,
                        Mode = AiExecutionReplayMode.ResumeIncomplete
                    });

                Assert.NotNull(replayResult);

                _output.WriteLine(
                    $"Replay result for ExecutionId='{handle.ExecutionId}': ReplayValid='{replayResult.ReplayValid}'.");

                Assert.True(
                    replayResult.ReplayValid,
                    "Replay should be valid when the live execution already exists.");

                Assert.True(
                    replayResult.ExecutionFound,
                    "Replay should report ExecutionFound when the live execution exists.");

                Assert.True(
                    replayResult.SnapshotFound,
                    "Replay should report SnapshotFound when the snapshot exists.");

                var restoredRecord = await dagStore.GetRecordAsync(
                    executionId);

                var restoredState = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(restoredRecord);
                Assert.NotNull(restoredState);

                Assert.Equal(
                    executionId,
                    restoredRecord!.ExecutionId);

                Assert.Equal(
                    executionId,
                    restoredState!.ExecutionId);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    restoredRecord.Status);

                Assert.True(
                    restoredRecord.IsTerminal);

                Assert.Equal(
                    scenario.StepCount,
                    restoredRecord.CompletedSteps.Count);

                await resolver.WarmAsync(
                    executionId,
                    restoredState!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    restoredState!,
                    resolver);

                AssertNoStaleClaims(
                    restoredState!);

                var afterReplayFingerprint =
                    await CreateReplayDeterminismFingerprintAsync(
                        scenario,
                        executionId,
                        restoredRecord,
                        restoredState!,
                        resolver);

                Assert.Equal(
                    beforeReplayFingerprint.Status,
                    afterReplayFingerprint.Status);

                Assert.Equal(
                    beforeReplayFingerprint.IsTerminal,
                    afterReplayFingerprint.IsTerminal);

                Assert.Equal(
                    beforeReplayFingerprint.CompletedSteps,
                    afterReplayFingerprint.CompletedSteps);

                Assert.Equal(
                    beforeReplayFingerprint.StepStatuses,
                    afterReplayFingerprint.StepStatuses);

                Assert.Equal(
                    beforeReplayFingerprint.RetryCounts,
                    afterReplayFingerprint.RetryCounts);

                Assert.Equal(
                    beforeReplayFingerprint.RequiredResolvedSteps,
                    afterReplayFingerprint.RequiredResolvedSteps);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupDagExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Creates a fully configured chaos test host.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateChaosHostAsync(
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateChaosOptions(scenario),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimePipelineBackgroundControllerChaosIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Creates a fully configured distributed chaos test host.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The created distributed test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateDistributedChaosHostAsync(
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateDistributedChaosOptions(scenario),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimePipelineBackgroundControllerChaosIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Creates a distributed chaos test host tuned to increase distributed claim races.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The created distributed claim-race test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateDistributedClaimRaceHostAsync(
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var options = CreateDistributedChaosOptions(
                scenario);

            options.RuntimeInstanceWorker.MaxStepsPerCycle = 1;

            options.RuntimeInstanceWorker.IdleDelay =
                TimeSpan.FromMilliseconds(1);

            options.RuntimeInstanceWorker.MaxCycles = 2000;

            options.PipelineBackgroundController.Distributed.WorkerCount = 5;

            // ------------------------------------------------------------
            // snapshot / replay persistence
            // ------------------------------------------------------------

            options.Snapshots.Enabled = true;
            options.Snapshots.Mongo.Enabled = true;
            options.Snapshots.Mongo.ConnectionString = "mongodb://localhost:27017";
            options.Snapshots.Mongo.DatabaseName = "multiplexed_ai_tests";
            options.Snapshots.Mongo.CollectionName =
                $"execution_snapshots_distributed_claim_race_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return await AiDagExecutionEngineFixture.CreateAsync(
                options,
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimePipelineBackgroundControllerChaosIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Creates runtime options for the background controller chaos scenario.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The configured engine options.</returns>
        private static AiEngineOptions CreateChaosOptions(
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Runtime",
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = scenario.MaxStepsPerCycle,
                    IdleDelay = scenario.WorkerIdleDelay,
                    MaxCycles = scenario.MaxWorkerCycles,
                    IgnoreConcurrencyConflicts = true
                },
                PipelineBackgroundController = new AiRuntimePipelineBackgroundControllerOptions
                {
                    MaxConcurrentRuns = scenario.MaxConcurrentRuns,
                    QueueCapacity = scenario.QueueCapacity,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false
                }
            };

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;

            options.Snapshots.Enabled = true;
            options.Snapshots.Mongo.Enabled = true;
            options.Snapshots.Mongo.ConnectionString = "mongodb://localhost:27017";
            options.Snapshots.Mongo.DatabaseName = "multiplexed_ai_tests";
            options.Snapshots.Mongo.CollectionName =
                $"execution_snapshots_background_controller_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        /// <summary>
        /// Creates distributed runtime options for the background controller chaos scenario.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The configured engine options.</returns>
        private static AiEngineOptions CreateDistributedChaosOptions(
            AiRuntimeChaosScenario scenario)
        {
            var options = CreateChaosOptions(
                scenario);

            options.PipelineBackgroundController.Distributed =
                new AiRuntimeDistributedExecutionOptions
                {
                    Enabled = true,
                    WorkerCount = 3,
                    StopOnFirstTerminal = true,
                    TerminalObservationTimeout = TimeSpan.FromSeconds(30)
                };

            return options;
        }

        /// <summary>
        /// Enqueues all scenario runs.
        /// </summary>
        /// <param name="controller">The background controller.</param>
        /// <param name="scenario">The scenario configuration.</param>
        /// <returns>The submitted run handles.</returns>
        private static async Task<IReadOnlyList<AiRuntimeWorkerRunHandle>> EnqueueScenarioRunsAsync(
            IAiRuntimePipelineBackgroundController controller,
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(controller);
            ArgumentNullException.ThrowIfNull(scenario);

            var handles = new List<AiRuntimeWorkerRunHandle>();

            for (var index = 0; index < scenario.RunCount; index++)
            {
                var handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId = $"candidate-{index:000}",
                            source = scenario.Name,
                            runIndex = index,
                            chaos = scenario.EnableChaos
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                handles.Add(handle);
            }

            return handles;
        }

        /// <summary>
        /// Validates that a handle is accepted after enqueue.
        /// </summary>
        /// <param name="handle">The run handle.</param>
        private static void AssertHandleAcceptedAfterEnqueue(
            AiRuntimeWorkerRunHandle handle)
        {
            ArgumentNullException.ThrowIfNull(handle);

            Assert.False(
                string.IsNullOrWhiteSpace(handle.RunId),
                "Each submitted run must have a controller RunId.");

            Assert.Contains(
                handle.Status,
                new[]
                {
                    AiRuntimeWorkerRunStatus.Queued,
                    AiRuntimeWorkerRunStatus.CreatingExecution,
                    AiRuntimeWorkerRunStatus.Running,
                    AiRuntimeWorkerRunStatus.Completed
                });
        }

        /// <summary>
        /// Waits for all submitted handles to complete.
        /// </summary>
        /// <param name="handles">The submitted run handles.</param>
        /// <param name="timeout">The completion timeout.</param>
        /// <returns>The final execution records.</returns>
        private static async Task<IReadOnlyList<AiExecutionRecord>> WaitForCompletionsAsync(
            IReadOnlyCollection<AiRuntimeWorkerRunHandle> handles,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(handles);

            Assert.NotEmpty(handles);

            var finals = await Task.WhenAll(
                handles.Select(handle =>
                    handle.Completion.WaitAsync(timeout)));

            Assert.Equal(
                handles.Count,
                finals.Length);

            return finals;
        }

        /// <summary>
        /// Validates completed executions, runtime namespaces, retry, retention compatibility,
        /// and stale claim cleanup.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <param name="handles">The submitted run handles.</param>
        /// <param name="finals">The terminal execution records.</param>
        /// <param name="dagStore">The DAG execution store.</param>
        /// <param name="resolver">The step resolver.</param>
        private static async Task AssertScenarioCompletedAsync(
            AiRuntimeChaosScenario scenario,
            IReadOnlyCollection<AiRuntimeWorkerRunHandle> handles,
            IReadOnlyCollection<AiExecutionRecord> finals,
            IAiDagExecutionStore dagStore,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(handles);
            ArgumentNullException.ThrowIfNull(finals);
            ArgumentNullException.ThrowIfNull(dagStore);
            ArgumentNullException.ThrowIfNull(resolver);

            Assert.Equal(scenario.RunCount, handles.Count);
            Assert.Equal(scenario.RunCount, finals.Count);

            Assert.All(
                finals,
                final =>
                {
                    Assert.NotNull(final);
                    Assert.False(string.IsNullOrWhiteSpace(final.ExecutionId));
                    Assert.True(final.IsTerminal);
                    Assert.Equal(AiExecutionStatus.Completed, final.Status);
                    Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);
                    Assert.Equal(scenario.PipelineName, final.PipelineName);
                });

            var runIds = handles
                .Select(handle => handle.RunId)
                .ToList();

            Assert.All(
                handles,
                handle =>
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(handle.RunId),
                        "Each submitted run must have a controller RunId.");

                    Assert.False(
                        string.IsNullOrWhiteSpace(handle.ExecutionId),
                        "Each submitted run must create a runtime ExecutionId.");

                    Assert.Equal(
                        AiRuntimeWorkerRunStatus.Completed,
                        handle.Status);
                });

            var executionIds = handles
                .Select(handle => handle.ExecutionId!)
                .ToList();

            Assert.Equal(
                handles.Count,
                runIds.Distinct(StringComparer.Ordinal).Count());

            Assert.Equal(
                handles.Count,
                executionIds.Distinct(StringComparer.Ordinal).Count());

            Assert.Empty(
                runIds.Intersect(
                    executionIds,
                    StringComparer.Ordinal));

            foreach (var handle in handles)
            {
                Assert.False(
                    string.Equals(
                        handle.RunId,
                        handle.ExecutionId,
                        StringComparison.Ordinal),
                    "RunId and ExecutionId must be different. RunId belongs to the controller lifecycle; ExecutionId belongs to the runtime execution namespace.");

                var matchingFinal = finals.Single(final =>
                    string.Equals(
                        final.ExecutionId,
                        handle.ExecutionId,
                        StringComparison.Ordinal));

                Assert.Equal(handle.ExecutionId, matchingFinal.ExecutionId);
                Assert.Equal(AiExecutionStatus.Completed, matchingFinal.Status);
                Assert.Equal(scenario.StepCount, matchingFinal.CompletedSteps.Count);

                var executionRecord = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionRecord);
                Assert.Equal(handle.ExecutionId, executionRecord!.ExecutionId);
                Assert.Equal(scenario.PipelineName, executionRecord.PipelineName);
                Assert.Equal(AiExecutionStatus.Completed, executionRecord.Status);
                Assert.Equal(scenario.StepCount, executionRecord.CompletedSteps.Count);
                Assert.True(executionRecord.IsTerminal);

                var executionState = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(executionState);
                Assert.Equal(handle.ExecutionId, executionState!.ExecutionId);
                Assert.Equal(scenario.PipelineName, executionState.PipelineName);
                Assert.NotNull(executionState.PipelineConfig);

                AssertPipelineConfigContainsRetention(
                    executionState,
                    scenario);

                AssertPipelineConfigContainsConcurrency(
                    executionState,
                    scenario);

                var runIdRecord = await dagStore.GetRecordAsync(
                    handle.RunId);

                Assert.Null(runIdRecord);

                var runIdState = await dagStore.GetStateAsync(
                    handle.RunId);

                Assert.Null(runIdState);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    executionState);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    executionState,
                    resolver);

                AssertNoStaleClaims(
                    executionState);

                AssertRetentionDidNotBreakState(
                    scenario,
                    executionState);
            }
        }

        /// <summary>
        /// Validates that retention configuration was propagated into execution state.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="scenario">The scenario configuration.</param>
        private static void AssertPipelineConfigContainsRetention(
            AiExecutionState state,
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(scenario);

            Assert.True(
                state.PipelineConfig.ContainsKey("retention"),
                "PipelineConfig must contain retention configuration.");

            var retention = state.PipelineConfig["retention"];

            Assert.True(
                AiRuntimeChaosConfigAssertHelper.ReadBoolean(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(retention, "enabled")),
                "Retention must be enabled.");

            var policies = AiRuntimeChaosConfigAssertHelper.GetConfigValue(
                retention,
                "policies");

            Assert.NotNull(policies);

            var policyNames = AiRuntimeChaosConfigAssertHelper.ReadStringArray(
                policies);

            Assert.Contains(
                "retention.compact.terminal",
                policyNames);

            Assert.Contains(
                "retention.evict.terminal",
                policyNames);

            Assert.Equal(
                scenario.RetentionArchiveReason,
                AiRuntimeChaosConfigAssertHelper.ReadString(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(retention, "archiveReason")));

            var trigger = AiRuntimeChaosConfigAssertHelper.GetConfigValue(
                retention,
                "trigger");

            Assert.NotNull(trigger);

            Assert.True(
                AiRuntimeChaosConfigAssertHelper.ReadBoolean(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(trigger, "enabled")),
                "Retention trigger must be enabled.");

            Assert.Equal(
                scenario.MaxCompletedStepsInState,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(trigger, "maxStepsInState")));

            Assert.Equal(
                scenario.MaxCompletedStepsInState,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(trigger, "maxCompletedStepsInState")));

            Assert.Equal(
                1,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(trigger, "maxInlinePayloadBytes")));
        }

        /// <summary>
        /// Validates that concurrency configuration was propagated into execution state.
        /// </summary>
        /// <param name="state">The execution state.</param>
        /// <param name="scenario">The scenario configuration.</param>
        private static void AssertPipelineConfigContainsConcurrency(
            AiExecutionState state,
            AiRuntimeChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(scenario);

            Assert.True(
                state.PipelineConfig.ContainsKey("concurrency"),
                "PipelineConfig must contain concurrency configuration.");

            var concurrency = state.PipelineConfig["concurrency"];

            Assert.True(
                AiRuntimeChaosConfigAssertHelper.ReadBoolean(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "enabled")),
                "Concurrency must be enabled.");

            Assert.Equal(
                scenario.MaxDegreeOfParallelism,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "maxDegreeOfParallelism")));

            Assert.Equal(
                scenario.MaxProviderConcurrency,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "maxProviderConcurrency")));

            Assert.Equal(
                60,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "leaseSeconds")));

            Assert.Equal(
                25,
                AiRuntimeChaosConfigAssertHelper.ReadInt32(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "defaultRetryAfterMs")));

            Assert.False(
                AiRuntimeChaosConfigAssertHelper.ReadBoolean(
                    AiRuntimeChaosConfigAssertHelper.GetConfigValue(concurrency, "jitter")),
                "Concurrency jitter should be disabled for deterministic chaos tests.");
        }

        /// <summary>
        /// Validates that required steps can be resolved after retention/compaction/eviction.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="resolver">The step resolver.</param>
        private static async Task AssertRequiredStepsResolvedAsync(
            AiRuntimeChaosScenario scenario,
            string executionId,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            foreach (var requiredStepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    requiredStepName,
                    state);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);
            }

            foreach (var flakyStepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    flakyStepName,
                    state);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);
                Assert.NotNull(step.Retry);

                Assert.True(
                    step.RetryState?.RetryCount >= 1,
                    $"Expected retry count >= 1 for step '{flakyStepName}'.");
            }
        }

        /// <summary>
        /// Validates that retention/compaction/eviction did not corrupt the execution state.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <param name="state">The execution state.</param>
        private static void AssertRetentionDidNotBreakState(
            AiRuntimeChaosScenario scenario,
            AiExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(state);

            Assert.NotNull(state.Steps);

            Assert.True(
                state.Steps.Count <= scenario.StepCount,
                "Hot state step count cannot exceed the original pipeline step count.");

            var hotCompletedSteps = state.Steps.Values
                .Count(step => step.Status == AiStepExecutionStatus.Completed);

            Assert.True(
                hotCompletedSteps <= scenario.StepCount,
                "Retention-enabled execution should remain valid even when completed steps are compacted or evicted from hot state.");

            if (state.Steps.Count == 0)
            {
                return;
            }

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.True(
                        step.Status == AiStepExecutionStatus.Completed ||
                        step.Status == AiStepExecutionStatus.Failed,
                        $"Unexpected retained terminal step status '{step.Status}'.");
                });
        }

        /// <summary>
        /// Validates that no hot step state keeps stale claim metadata.
        /// </summary>
        /// <param name="state">The execution state.</param>
        private static void AssertNoStaleClaims(
            AiExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.Null(step.ClaimedBy);
                    Assert.Null(step.ClaimToken);
                    Assert.Null(step.ClaimedAtUtc);
                    Assert.Null(step.LeaseExpiresAtUtc);
                });
        }

        /// <summary>
        /// Validates worker metrics were recorded.
        /// </summary>
        /// <param name="metrics">The metrics facade.</param>
        private static void AssertWorkerMetricsRecorded(
            IAiRuntimeMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(metrics);

            var cycles = metrics.Worker.GetCyclesByRuntimeInstance();
            var terminal = metrics.Worker.GetTerminalByStatus();

            Assert.NotEmpty(cycles);

            Assert.True(
                cycles.Values.Sum() > 0,
                "Expected at least one worker cycle metric.");

            Assert.True(
                terminal.TryGetValue(
                    AiExecutionStatus.Completed.ToString(),
                    out var completedCount),
                "Expected worker terminal metrics for Completed executions.");

            Assert.True(
                completedCount > 0,
                "Expected at least one Completed terminal metric.");
        }

        /// <summary>
        /// Validates trace timeline events were recorded.
        /// </summary>
        /// <param name="timeline">The trace timeline.</param>
        /// <param name="handles">The submitted run handles.</param>
        private static void AssertTraceTimelineRecorded(
            IAiTraceTimeline timeline,
            IReadOnlyCollection<AiRuntimeWorkerRunHandle> handles)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(handles);

            foreach (var handle in handles)
            {
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var events = timeline.Get(handle.ExecutionId!);

                Assert.NotEmpty(events);

                Assert.Contains(
                    events,
                    traceEvent =>
                        string.Equals(traceEvent.Category, "step", StringComparison.OrdinalIgnoreCase));

                Assert.Contains(
                    events,
                    traceEvent =>
                        string.Equals(traceEvent.Category, "dag-store", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Renders trace timeline events for all executions created by the scenario.
        /// </summary>
        /// <param name="timeline">The trace timeline.</param>
        /// <param name="handles">The submitted run handles.</param>
        private void RenderTraceTimeline(
            IAiTraceTimeline timeline,
            IReadOnlyCollection<AiRuntimeWorkerRunHandle> handles)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(handles);

            foreach (var handle in handles)
            {
                if (string.IsNullOrWhiteSpace(handle.ExecutionId))
                {
                    continue;
                }

                var events = timeline.Get(handle.ExecutionId);

                _output.WriteLine(
                    $"Trace timeline for ExecutionId='{handle.ExecutionId}', RunId='{handle.RunId}', Events='{events.Count}'.");

                foreach (var traceEvent in events.Take(30))
                {
                    _output.WriteLine(
                        $"{traceEvent.Category} | {traceEvent.Name} | StepId='{traceEvent.StepId}'");
                }

                Assert.NotEmpty(events);
            }
        }

        /// <summary>
        /// Deletes only the live DAG execution bundle while preserving terminal snapshots.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="executionId">The execution identifier.</param>
        private static async Task CleanupDagExecutionAsync(
            IServiceProvider serviceProvider,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var dagStore = serviceProvider.GetRequiredService<IAiDagExecutionStore>();

            await dagStore.DeleteExecutionBundleAsync(
                executionId);
        }

        /// <summary>
        /// Cleans all executions created by submitted run handles.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="handles">The submitted run handles.</param>
        private static async Task CleanupExecutionsAsync(
            IServiceProvider serviceProvider,
            IReadOnlyCollection<AiRuntimeWorkerRunHandle> handles)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentNullException.ThrowIfNull(handles);

            var dagStore = serviceProvider.GetRequiredService<IAiDagExecutionStore>();

            foreach (var handle in handles)
            {
                if (string.IsNullOrWhiteSpace(handle.ExecutionId))
                {
                    continue;
                }

                await dagStore.DeleteExecutionBundleAsync(
                    handle.ExecutionId);
            }
        }

        /// <summary>
        /// Runs a controller scenario and creates a deterministic completion fingerprint.
        /// </summary>
        /// <param name="host">The test host.</param>
        /// <param name="scenario">The scenario configuration.</param>
        /// <param name="candidateId">The candidate identifier.</param>
        /// <param name="source">The scenario source value.</param>
        /// <returns>The deterministic completion fingerprint.</returns>
        private async Task<ControllerCompletionFingerprint> RunScenarioAndCreateCompletionFingerprintAsync(
            AiDagExecutionEngineTestHost host,
            AiRuntimeChaosScenario scenario,
            string candidateId,
            string source)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: scenario.PipelineName,
                        pipelineDefinition: scenario.PipelineDefinition,
                        input: new
                        {
                            candidateId,
                            source
                        },
                        source: scenario.Name));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(scenario.StepCount, final.CompletedSteps.Count);

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.Equal(handle.ExecutionId, final.ExecutionId);

                var state = await dagStore.GetStateAsync(
                    handle.ExecutionId!);

                Assert.NotNull(state);

                await resolver.WarmAsync(
                    handle.ExecutionId!,
                    state!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    handle.ExecutionId!,
                    state!,
                    resolver);

                AssertNoStaleClaims(
                    state!);

                return await CreateControllerCompletionFingerprintAsync(
                    scenario,
                    handle.ExecutionId!,
                    final,
                    state!,
                    resolver);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionsAsync(
                        host.ServiceProvider,
                        new[] { handle });
                }
            }
        }

        /// <summary>
        /// Creates a deterministic completion fingerprint for controller execution comparison.
        /// </summary>
        /// <param name="scenario">The scenario configuration.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="record">The terminal execution record.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="resolver">The step resolver.</param>
        /// <returns>The deterministic completion fingerprint.</returns>
        private static async Task<ControllerCompletionFingerprint> CreateControllerCompletionFingerprintAsync(
            AiRuntimeChaosScenario scenario,
            string executionId,
            AiExecutionRecord record,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            var stepStatuses = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var step in scenario.PipelineDefinition.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Name))
                {
                    continue;
                }

                var resolvedStatus = await resolver.GetStepStatusAsync(
                    executionId,
                    step.Name,
                    state,
                    CancellationToken.None);

                Assert.NotNull(resolvedStatus);

                stepStatuses[step.Name] =
                    resolvedStatus!.Status.ToString();
            }

            var retryCounts = new SortedDictionary<string, int>(
                StringComparer.Ordinal);

            foreach (var retriedStepName in scenario.ExpectedRetriedSteps)
            {
                var fullStep = await resolver.GetStepAsync(
                    executionId,
                    retriedStepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(fullStep);
                Assert.Equal(AiStepExecutionStatus.Completed, fullStep!.Status);

                retryCounts[retriedStepName] =
                    fullStep.RetryState?.RetryCount ?? 0;
            }

            var requiredResolvedSteps = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var requiredStepName in scenario.RequiredResolvedSteps)
            {
                var fullStep = await resolver.GetStepAsync(
                    executionId,
                    requiredStepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(fullStep);

                requiredResolvedSteps[requiredStepName] =
                    fullStep!.Status.ToString();
            }

            return new ControllerCompletionFingerprint
            {
                Status = record.Status.ToString(),
                IsTerminal = record.IsTerminal,
                CompletedStepCount = record.CompletedSteps.Count,
                StepStatuses = stepStatuses,
                RetryCounts = retryCounts,
                RequiredResolvedSteps = requiredResolvedSteps
            };
        }

        /// <summary>
        /// Represents a parameterized runtime chaos scenario.
        /// </summary>
        private sealed class AiRuntimeChaosScenario
        {
            public required string Name { get; init; }

            public required string PipelineName { get; init; }

            public required AiPipelineDefinition PipelineDefinition { get; init; }

            public required string RetentionArchiveReason { get; init; }

            public int RunCount { get; init; }

            public int StepCount { get; init; }

            public int MaxConcurrentRuns { get; init; }

            public int MaxStepsPerCycle { get; init; }

            public int MaxWorkerCycles { get; init; }

            public int QueueCapacity { get; init; }

            public int MaxDegreeOfParallelism { get; init; }

            public int MaxProviderConcurrency { get; init; }

            public int MaxCompletedStepsInState { get; init; }

            public bool EnableChaos { get; init; }

            public TimeSpan WorkerIdleDelay { get; init; }

            public TimeSpan Timeout { get; init; }

            public IReadOnlyCollection<string> RequiredResolvedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> ExpectedRetriedSteps { get; init; } =
                Array.Empty<string>();

            public static AiRuntimeChaosScenario Small()
            {
                var pipelineName = $"background-controller-small-chaos-{Guid.NewGuid():N}";

                const int stepCount = 12;
                const int maxCompletedStepsInState = 6;
                const int maxDegreeOfParallelism = 4;
                const int maxProviderConcurrency = 2;
                const string archiveReason = "background-controller-small-retention-test";

                var pipeline = CreateParameterizedPipelineDefinition(
                    pipelineName,
                    stepCount,
                    flakyStepInterval: 6,
                    maxCompletedStepsInState,
                    maxDegreeOfParallelism,
                    maxProviderConcurrency,
                    archiveReason);

                return new AiRuntimeChaosScenario
                {
                    Name = "small-runtime-validation",
                    PipelineName = pipelineName,
                    PipelineDefinition = pipeline,
                    RetentionArchiveReason = archiveReason,
                    RunCount = 2,
                    StepCount = stepCount,
                    MaxConcurrentRuns = 2,
                    MaxStepsPerCycle = 3,
                    MaxWorkerCycles = 300,
                    QueueCapacity = 16,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    MaxProviderConcurrency = maxProviderConcurrency,
                    MaxCompletedStepsInState = maxCompletedStepsInState,
                    EnableChaos = false,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(10),
                    Timeout = TimeSpan.FromSeconds(60),
                    RequiredResolvedSteps = new[]
                    {
                        "chaos-step-01",
                        "final-join-step"
                    },
                    ExpectedRetriedSteps = new[]
                    {
                        "chaos-step-01"
                    }
                };
            }

            public static AiRuntimeChaosScenario Chaos()
            {
                var pipelineName = $"background-controller-full-chaos-{Guid.NewGuid():N}";

                const int stepCount = 50;
                const int maxCompletedStepsInState = 10;
                const int maxDegreeOfParallelism = 8;
                const int maxProviderConcurrency = 2;
                const string archiveReason = "background-controller-chaos-retention-test";

                var pipeline = CreateParameterizedPipelineDefinition(
                    pipelineName,
                    stepCount,
                    flakyStepInterval: 7,
                    maxCompletedStepsInState,
                    maxDegreeOfParallelism,
                    maxProviderConcurrency,
                    archiveReason);

                return new AiRuntimeChaosScenario
                {
                    Name = "full-runtime-chaos",
                    PipelineName = pipelineName,
                    PipelineDefinition = pipeline,
                    RetentionArchiveReason = archiveReason,
                    RunCount = 4,
                    StepCount = stepCount,
                    MaxConcurrentRuns = 3,
                    MaxStepsPerCycle = 6,
                    MaxWorkerCycles = 1000,
                    QueueCapacity = 32,
                    MaxDegreeOfParallelism = maxDegreeOfParallelism,
                    MaxProviderConcurrency = maxProviderConcurrency,
                    EnableChaos = true,
                    MaxCompletedStepsInState = maxCompletedStepsInState,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(5),
                    Timeout = TimeSpan.FromSeconds(120),
                    RequiredResolvedSteps = new[]
                    {
                        "chaos-step-01",
                        "chaos-step-07",
                        "chaos-step-14",
                        "final-join-step"
                    },
                    ExpectedRetriedSteps = new[]
                    {
                        "chaos-step-01",
                        "chaos-step-07",
                        "chaos-step-14",
                        "chaos-step-21",
                        "chaos-step-28",
                        "chaos-step-35",
                        "chaos-step-42"
                    }
                };
            }
        }

        /// <summary>
        /// Creates a parameterized DAG pipeline definition for chaos testing.
        /// </summary>
        private static AiPipelineDefinition CreateParameterizedPipelineDefinition(
            string pipelineName,
            int stepCount,
            int flakyStepInterval,
            int maxCompletedStepsInState,
            int maxDegreeOfParallelism,
            int maxProviderConcurrency,
            string archiveReason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveReason);

            if (stepCount < 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stepCount),
                    "Step count must be at least 4.");
            }

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "chaos-step-01",
                    StepKey = "chaos.flaky-provider",
                    Order = 1,
                    Config = CreateStepConfig(
                        pipelineName,
                        index: 1,
                        isFlaky: true)
                }
            };

            for (var index = 2; index < stepCount; index++)
            {
                var isFlaky =
                    index % flakyStepInterval == 0;

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = $"chaos-step-{index:00}",
                        StepKey = isFlaky
                            ? "chaos.flaky-provider"
                            : "hello-world",
                        Order = index,
                        DependsOn = new[] { "chaos-step-01" },
                        Config = CreateStepConfig(
                            pipelineName,
                            index,
                            isFlaky)
                    });
            }

            steps.Add(
                new AiPipelineStepDefinition
                {
                    Name = "final-join-step",
                    StepKey = "hello-world",
                    Order = stepCount,
                    DependsOn = Enumerable.Range(2, stepCount - 2)
                        .Select(index => $"chaos-step-{index:00}")
                        .ToArray(),
                    Config = new Dictionary<string, object?>
                    {
                        ["provider"] = "openai",
                        ["model"] = "gpt-4.1",
                        ["operation"] = "llm.compose",
                        ["delayMs"] = 5
                    }
                });

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = CreateConcurrencyConfig(
                        maxDegreeOfParallelism,
                        maxProviderConcurrency),
                    ["retention"] = CreateRetentionConfig(
                        maxCompletedStepsInState,
                        archiveReason)
                },
                Steps = steps
            };
        }

        /// <summary>
        /// Creates concurrency configuration.
        /// </summary>
        private static Dictionary<string, object?> CreateConcurrencyConfig(
            int maxDegreeOfParallelism,
            int maxProviderConcurrency)
        {
            Assert.True(maxDegreeOfParallelism > 0);
            Assert.True(maxProviderConcurrency > 0);

            return new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["maxDegreeOfParallelism"] = maxDegreeOfParallelism,
                ["maxProviderConcurrency"] = maxProviderConcurrency,
                ["leaseSeconds"] = 60,
                ["defaultRetryAfterMs"] = 25,
                ["jitter"] = false
            };
        }

        /// <summary>
        /// Creates retention configuration using the runtime retention policy model.
        /// </summary>
        private static Dictionary<string, object?> CreateRetentionConfig(
            int maxCompletedStepsInState,
            string archiveReason)
        {
            if (maxCompletedStepsInState <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxCompletedStepsInState),
                    "Max completed steps in state must be greater than zero.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(archiveReason);

            return new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["policies"] = new[]
                {
                    "retention.compact.terminal",
                    "retention.evict.terminal"
                },
                ["archiveReason"] = archiveReason,
                ["trigger"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxStepsInState"] = maxCompletedStepsInState,
                    ["maxCompletedStepsInState"] = maxCompletedStepsInState,
                    ["maxInlinePayloadBytes"] = 1
                }
            };
        }

        /// <summary>
        /// Creates per-step configuration.
        /// </summary>
        private static Dictionary<string, object?> CreateStepConfig(
            string pipelineName,
            int index,
            bool isFlaky)
        {
            var config = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["delayMs"] = isFlaky ? 15 : 3
            };

            if (isFlaky)
            {
                config["attemptKey"] = $"{pipelineName}:chaos-step-{index:00}";
                config["retry"] = new Dictionary<string, object?>
                {
                    ["maxRetries"] = 2,
                    ["strategy"] = "Fixed",
                    ["baseDelayMs"] = 25,
                    ["maxDelayMs"] = 25,
                    ["jitter"] = false
                };
            }

            return config;
        }

        /// <summary>
        /// Waits until a snapshot document exists for the specified execution, while giving
        /// the engine a chance to run finalization cycles after the controller completed.
        /// </summary>
        private static async Task<AiExecutionSnapshotDocument<ExecutionContextSnapshot>> WaitForSnapshotAfterFinalizationAsync(
            AiDagExecutionEngine engine,
            IAiExecutionSnapshotStore<ExecutionContextSnapshot> snapshotStore,
            string executionId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(snapshotStore);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var deadline = DateTime.UtcNow.Add(timeout);
            Exception? lastException = null;

            while (DateTime.UtcNow < deadline)
            {
                var snapshot = await snapshotStore.GetAsync(
                    executionId);

                if (snapshot is not null)
                {
                    return snapshot;
                }

                try
                {
                    await engine.ExecuteNextAsync(
                        executionId);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(50));
            }

            if (lastException is not null)
            {
                throw new TimeoutException(
                    $"Snapshot for execution '{executionId}' was not created within '{timeout}'. Last ExecuteNextAsync error: '{lastException.Message}'.",
                    lastException);
            }

            throw new TimeoutException(
                $"Snapshot for execution '{executionId}' was not created within '{timeout}'. ExecuteNextAsync did not throw.");
        }

        /// <summary>
        /// Creates a deterministic fingerprint of a completed execution that can be compared
        /// before deletion and after replay restore.
        /// </summary>
        private static async Task<ReplayDeterminismFingerprint> CreateReplayDeterminismFingerprintAsync(
            AiRuntimeChaosScenario scenario,
            string executionId,
            AiExecutionRecord record,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            var stepStatuses = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var step in scenario.PipelineDefinition.Steps)
            {
                if (string.IsNullOrWhiteSpace(step.Name))
                {
                    continue;
                }

                var resolvedStatus = await resolver.GetStepStatusAsync(
                    executionId,
                    step.Name,
                    state,
                    CancellationToken.None);

                Assert.NotNull(resolvedStatus);

                stepStatuses[step.Name] = resolvedStatus!.Status.ToString();
            }

            var retryCounts = new SortedDictionary<string, int>(
                StringComparer.Ordinal);

            foreach (var retriedStepName in scenario.ExpectedRetriedSteps)
            {
                var fullStep = await resolver.GetStepAsync(
                    executionId,
                    retriedStepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(fullStep);
                Assert.Equal(AiStepExecutionStatus.Completed, fullStep!.Status);

                retryCounts[retriedStepName] =
                    fullStep.RetryState?.RetryCount ?? 0;
            }

            var requiredResolvedSteps = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var requiredStepName in scenario.RequiredResolvedSteps)
            {
                var fullStep = await resolver.GetStepAsync(
                    executionId,
                    requiredStepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(fullStep);

                requiredResolvedSteps[requiredStepName] =
                    fullStep!.Status.ToString();
            }

            return new ReplayDeterminismFingerprint
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName ?? string.Empty,
                Status = record.Status.ToString(),
                IsTerminal = record.IsTerminal,
                CompletedSteps = record.CompletedSteps
                    .OrderBy(step => step, StringComparer.Ordinal)
                    .ToArray(),
                StepStatuses = stepStatuses,
                RetryCounts = retryCounts,
                RequiredResolvedSteps = requiredResolvedSteps
            };
        }

        /// <summary>
        /// Stable comparable execution fingerprint used by replay determinism tests.
        /// </summary>
        private sealed class ReplayDeterminismFingerprint
        {
            public required string ExecutionId { get; init; }

            public required string PipelineName { get; init; }

            public required string Status { get; init; }

            public required bool IsTerminal { get; init; }

            public required IReadOnlyList<string> CompletedSteps { get; init; }

            public required IReadOnlyDictionary<string, string> StepStatuses { get; init; }

            public required IReadOnlyDictionary<string, int> RetryCounts { get; init; }

            public required IReadOnlyDictionary<string, string> RequiredResolvedSteps { get; init; }
        }

        /// <summary>
        /// Stable comparable execution fingerprint used by controller deterministic comparison tests.
        /// </summary>
        private sealed class ControllerCompletionFingerprint
        {
            public required string Status { get; init; }

            public required bool IsTerminal { get; init; }

            public required int CompletedStepCount { get; init; }

            public required IReadOnlyDictionary<string, string> StepStatuses { get; init; }

            public required IReadOnlyDictionary<string, int> RetryCounts { get; init; }

            public required IReadOnlyDictionary<string, string> RequiredResolvedSteps { get; init; }
        }
    }

    /// <summary>
    /// Helper class for reading and asserting pipeline config values that may be
    /// represented either as dictionaries before persistence or as JsonElement values
    /// after reload from the store.
    /// </summary>
    internal static class AiRuntimeChaosConfigAssertHelper
    {
        /// <summary>
        /// Gets a named property from either a dictionary-backed object or a JsonElement.
        /// </summary>
        public static object? GetConfigValue(
            object? source,
            string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            if (source is IDictionary<string, object?> dictionary)
            {
                Assert.True(
                    dictionary.TryGetValue(key, out var value),
                    $"Expected config key '{key}'.");

                return value;
            }

            if (source is JsonElement jsonElement)
            {
                Assert.True(
                    jsonElement.ValueKind == JsonValueKind.Object,
                    $"Expected JsonElement object when reading key '{key}'.");

                Assert.True(
                    jsonElement.TryGetProperty(key, out var property),
                    $"Expected JSON config key '{key}'.");

                return property;
            }

            throw new InvalidOperationException(
                $"Unsupported config source type '{source?.GetType().FullName ?? "null"}' when reading key '{key}'.");
        }

        /// <summary>
        /// Reads a boolean value from either a dictionary-backed value or a JsonElement.
        /// </summary>
        public static bool ReadBoolean(
            object? value)
        {
            if (value is bool boolean)
            {
                return boolean;
            }

            if (value is JsonElement jsonElement)
            {
                return jsonElement.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => jsonElement.GetBoolean()
                };
            }

            return Convert.ToBoolean(value);
        }

        /// <summary>
        /// Reads an integer value from either a dictionary-backed value or a JsonElement.
        /// </summary>
        public static int ReadInt32(
            object? value)
        {
            if (value is int integer)
            {
                return integer;
            }

            if (value is long longValue)
            {
                return Convert.ToInt32(longValue);
            }

            if (value is JsonElement jsonElement)
            {
                return jsonElement.GetInt32();
            }

            return Convert.ToInt32(value);
        }

        /// <summary>
        /// Reads a string value from either a dictionary-backed value or a JsonElement.
        /// </summary>
        public static string? ReadString(
            object? value)
        {
            if (value is string text)
            {
                return text;
            }

            if (value is JsonElement jsonElement)
            {
                return jsonElement.GetString();
            }

            return Convert.ToString(value);
        }

        /// <summary>
        /// Reads a string array from either a dictionary-backed value or a JsonElement.
        /// </summary>
        public static IReadOnlyCollection<string> ReadStringArray(
            object? value)
        {
            if (value is string[] stringArray)
            {
                return stringArray;
            }

            if (value is IEnumerable<string> enumerable)
            {
                return enumerable.ToArray();
            }

            if (value is JsonElement jsonElement)
            {
                Assert.Equal(
                    JsonValueKind.Array,
                    jsonElement.ValueKind);

                return jsonElement
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray();
            }

            throw new InvalidOperationException(
                $"Unsupported string array config value type '{value?.GetType().FullName ?? "null"}'.");
        }
    }

    /// <summary>
    /// Test step that fails once per execution-specific attempt key and then succeeds.
    /// </summary>
    [AiStep("chaos.flaky-provider")]
    public sealed class ChaosFlakyProviderStep : IAiStep
    {
        private static readonly ConcurrentDictionary<string, int> Attempts =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Name => "chaos.flaky-provider";

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper = context.GetHelper();

            var configuredAttemptKey = await helper.GetConfigAsync<string>(
                "attemptKey",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(configuredAttemptKey))
            {
                throw new InvalidOperationException(
                    "Missing required config value 'attemptKey'.");
            }

            var executionId = context.Record.ExecutionId;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new InvalidOperationException(
                    "ExecutionId is required for execution-scoped flaky attempt tracking.");
            }

            var attemptKey = configuredAttemptKey + ":" + executionId;

            var delayMs = await helper.GetConfigAsync<int?>(
                "delayMs",
                cancellationToken).ConfigureAwait(false) ?? 0;

            if (delayMs > 0)
            {
                await Task.Delay(
                    delayMs,
                    cancellationToken).ConfigureAwait(false);
            }

            var attempt = Attempts.AddOrUpdate(
                attemptKey,
                1,
                (_, current) => current + 1);

            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    $"Intentional chaos first-attempt failure for step '{attemptKey}'.");
            }

            return AiStepResult.Ok(
                output: $"Recovered after attempt {attempt}.",
                data: helper.ToDictionary(new
                {
                    attemptKey,
                    attempt,
                    executionId
                }));
        }
    }
}