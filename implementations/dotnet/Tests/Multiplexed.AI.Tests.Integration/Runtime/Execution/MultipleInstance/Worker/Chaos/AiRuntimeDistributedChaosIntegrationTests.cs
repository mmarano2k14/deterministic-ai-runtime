using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Metrics.Store;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Observability.Tracing.Store;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Runtime.Observability.Ledger.DI;
using Multiplexed.AI.Runtime.Observability.Tracing.Stores.Mongo;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using System.Collections.Concurrent;
using Xunit;
using Xunit.Abstractions;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker.Chaos
{
    /// <summary>
    /// High-pressure distributed chaos integration tests for multi-runtime-instance execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests validate that multiple runtime workers can safely advance the same
    /// execution identifier under retry, distributed concurrency, retention compaction,
    /// retention eviction, snapshot persistence, replay restore, and deterministic
    /// convergence pressure.
    /// </para>
    /// <para>
    /// The scenario model is intentionally parameterized so the same validation path can
    /// be reused for 100-step, 500-step, or larger distributed chaos executions without
    /// rewriting the test flow.
    /// </para>
    /// </remarks>
    [Collection("redis")]
    public sealed class AiRuntimeDistributedChaosIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeDistributedChaosIntegrationTests"/> class.
        /// </summary>
        /// <param name="output">The xUnit output helper.</param>
        public AiRuntimeDistributedChaosIntegrationTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Runs a 100-step distributed chaos execution with retry, concurrency,
        /// compaction, eviction, terminal snapshot persistence, replay restore,
        /// and deterministic replay comparison.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Run_100_Steps_With_Retry_Concurrency_Compaction_Eviction_And_Replay()
        {
            await RunDistributedChaosScenarioAsync(
                DistributedChaosScenario.Steps100());
        }

        /// <summary>
        /// Runs a 500-step distributed chaos execution with retry, concurrency,
        /// retention compaction, eviction, snapshot persistence, replay restore,
        /// and deterministic replay validation.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Run_500_Steps_With_Retry_Concurrency_Compaction_Eviction_And_Replay()
        {
            await RunDistributedChaosScenarioAsync(
                DistributedChaosScenario.Steps500());
        }

        [RedisFact]
        public async Task DistributedChaos_Should_Run_500_Steps_With_Aggressive_Retention_Eviction()
        {
            await RunDistributedChaosScenarioAsync(
                DistributedChaosScenario.Steps500AggressiveRetention());
        }

        /// <summary>
        /// Repeatedly runs the aggressive 500-step distributed chaos scenario to detect
        /// intermittent retention, snapshot, replay, and hot-state regression failures.
        /// </summary>
        [RedisFact(Skip = "Long-running stress validation test.")]
        //[RedisFact]
        public async Task DistributedChaos_Should_Run_500_Steps_With_Aggressive_Retention_Eviction_Repeatedly()
        {
            const int iterations = 20;

            for (var iteration = 1; iteration <= iterations; iteration++)
            {
                _output.WriteLine(
                    $"Starting aggressive retention chaos iteration {iteration}/{iterations}.");

                await RunDistributedChaosScenarioAsync(
                    DistributedChaosScenario.Steps500AggressiveRetention());

                _output.WriteLine(
                    $"Completed aggressive retention chaos iteration {iteration}/{iterations}.");
            }
        }

        /// <summary>
        /// Validates run-id-correlated controller queue decision ledger events.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Record_Run_Queued_And_Dequeued_Ledger_Events()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            ledger = true
                        }));

                Assert.NotNull(handle);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));

                var final = await handle.Completion.WaitAsync(scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);

                var runEntries = await ledger.GetByExecutionAsync(handle.RunId);

                Assert.NotEmpty(runEntries);

                AssertLedgerContains(
                    runEntries,
                    AiDecisionLedgerCategory.Run,
                    AiEngineEvents.Run.Queued);

                AssertLedgerContains(
                    runEntries,
                    AiDecisionLedgerCategory.Run,
                    AiEngineEvents.Run.Dequeued);

                var queued = runEntries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        x.EventType == AiEngineEvents.Run.Queued);

                var dequeued = runEntries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        x.EventType == AiEngineEvents.Run.Dequeued);

                Assert.Equal(AiDecisionLedgerOutcome.Persisted, queued.Outcome);
                Assert.Equal(AiDecisionLedgerOutcome.Started, dequeued.Outcome);

                Assert.Equal(handle.RunId, queued.CorrelationContext.ExecutionId);
                Assert.Equal(handle.RunId, dequeued.CorrelationContext.ExecutionId);

                Assert.Equal(handle.RunId, queued.CorrelationContext.RunId);
                Assert.Equal(handle.RunId, dequeued.CorrelationContext.RunId);

                Assert.Equal("pipeline-background-controller", queued.CorrelationContext.WorkerId);
                Assert.Equal("pipeline-background-controller", dequeued.CorrelationContext.WorkerId);

                Assert.NotNull(queued.Metadata);
                Assert.NotNull(dequeued.Metadata);

                Assert.Equal(handle.RunId, queued.Metadata!["run.id"]);
                Assert.Equal(handle.RunId, dequeued.Metadata!["run.id"]);

                Assert.Equal(scenario.PipelineName, queued.Metadata!["pipeline.name"]);
                Assert.Equal(scenario.PipelineName, dequeued.Metadata!["pipeline.name"]);

                Assert.DoesNotContain(
                    runEntries,
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        (x.EventType == AiEngineEvents.Run.Started || x.EventType == AiEngineEvents.Run.Completed));

                _output.WriteLine("");
                _output.WriteLine("RUN-ID-CORRELATED QUEUE LEDGER EVENTS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in runEntries
                    .Where(x => x.Category == AiDecisionLedgerCategory.Run)
                    .OrderBy(x => x.TimestampUtc))
                {
                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.Category,-12} | " +
                        $"{entry.EventType,-30} | " +
                        $"{entry.Outcome,-12} | " +
                        $"ExecutionId={entry.CorrelationContext.ExecutionId} | " +
                        $"CorrelationId={entry.CorrelationContext.CorrelationId} | " +
                        $"Worker={entry.CorrelationContext.WorkerId}");
                }
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
        /// Validates execution-correlated queue pause and resume decision ledger events.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Record_Queue_Pause_And_Resume_Ledger_Events()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            ledger = true
                        }));

                Assert.NotNull(handle);

                while (string.IsNullOrWhiteSpace(handle.ExecutionId))
                {
                    await Task.Delay(25);
                }

                await controller.PauseQueueAsync(
                    reason: "test-pause",
                    requestedBy: "integration-test");

                await controller.ResumeQueueAsync(
                    requestedBy: "integration-test");

                var final = await handle.Completion.WaitAsync(scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var entries = await ledger.GetByExecutionAsync(handle.ExecutionId!);

                Assert.NotEmpty(entries);

                AssertLedgerContains(entries, AiDecisionLedgerCategory.Queue, AiEngineEvents.Queue.Paused);
                AssertLedgerContains(entries, AiDecisionLedgerCategory.Queue, AiEngineEvents.Queue.Resumed);

                var paused = entries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Queue &&
                        x.EventType == AiEngineEvents.Queue.Paused);

                var resumed = entries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Queue &&
                        x.EventType == AiEngineEvents.Queue.Resumed);

                Assert.Equal(AiDecisionLedgerOutcome.Applied, paused.Outcome);
                Assert.Equal(AiDecisionLedgerOutcome.Applied, resumed.Outcome);

                Assert.Equal(handle.ExecutionId, paused.CorrelationContext.ExecutionId);
                Assert.Equal(handle.ExecutionId, resumed.CorrelationContext.ExecutionId);

                Assert.Equal(handle.RunId, paused.CorrelationContext.RunId);
                Assert.Equal(handle.RunId, resumed.CorrelationContext.RunId);

                Assert.Equal("pipeline-background-controller", paused.CorrelationContext.WorkerId);
                Assert.Equal("pipeline-background-controller", resumed.CorrelationContext.WorkerId);

                Assert.Equal("test-pause", paused.Reason);
                Assert.Equal(
                    "Pipeline controller queue resumed.",
                    resumed.Reason);

                Assert.NotNull(paused.Metadata);
                Assert.NotNull(resumed.Metadata);

                Assert.Equal(
                    "integration-test",
                    paused.Metadata!["requested.by"]);

                Assert.Equal(
                    "integration-test",
                    resumed.Metadata!["requested.by"]);

                Assert.Contains(
                    "paused.at.utc",
                    paused.Metadata!.Keys);

                Assert.Contains(
                    "paused.since.utc",
                    resumed.Metadata!.Keys);

                _output.WriteLine("");
                _output.WriteLine("EXECUTION-CORRELATED QUEUE PAUSE / RESUME LEDGER EVENTS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in entries
                    .Where(x => x.Category == AiDecisionLedgerCategory.Queue)
                    .OrderBy(x => x.TimestampUtc))
                {
                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.Category,-12} | " +
                        $"{entry.EventType,-30} | " +
                        $"{entry.Outcome,-12} | " +
                        $"ExecutionId={entry.CorrelationContext.ExecutionId} | " +
                        $"CorrelationId={entry.CorrelationContext.CorrelationId} | " +
                        $"Worker={entry.CorrelationContext.WorkerId} | " +
                        $"Reason={entry.Reason}");
                }
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
        /// Runs a distributed chaos execution and prints the full execution-correlated
        /// decision ledger grouped by category, event type, outcome, and chronological timeline.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This test is intentionally diagnostic. It is designed to validate and display the
        /// complete decision ledger produced by a real distributed DAG execution.
        /// </para>
        /// <para>
        /// The scenario exercises distributed workers, step claiming, step execution,
        /// retry scheduling, retention evaluation, compaction or eviction, finalization,
        /// execution completion, and policy decisions.
        /// </para>
        /// <para>
        /// The test is skipped by default because it produces verbose output and depends on
        /// Redis-backed distributed execution infrastructure.
        /// </para>
        /// </remarks>
        [RedisFact]
        public async Task DistributedChaos_Should_Print_Execution_Correlated_Decision_Ledger()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            audit = true
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var entries = await ledger.GetByExecutionAsync(
                    executionId);

                Assert.NotEmpty(entries);

                _output.WriteLine("");
                _output.WriteLine("============================================================");
                _output.WriteLine("EXECUTION-CORRELATED DECISION LEDGER");
                _output.WriteLine("============================================================");
                _output.WriteLine($"ExecutionId: {executionId}");
                _output.WriteLine($"Pipeline:    {scenario.PipelineName}");
                _output.WriteLine($"Steps:       {scenario.StepCount}");
                _output.WriteLine($"Workers:     {scenario.WorkerCount}");
                _output.WriteLine($"Events:      {entries.Count}");
                _output.WriteLine("");

                _output.WriteLine("SUMMARY BY CATEGORY / EVENT / OUTCOME");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in entries
                    .GroupBy(entry => new
                    {
                        entry.Category,
                        entry.EventType,
                        entry.Outcome
                    })
                    .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                    .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome.ToString(), StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.EventType,-40} | {group.Key.Outcome,-12} | Count={group.Count()}");
                }

                _output.WriteLine("");
                _output.WriteLine("TIMELINE");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in entries
                    .OrderBy(entry => entry.TimestampUtc)
                    .ThenBy(entry => entry.EventType, StringComparer.Ordinal))
                {
                    var metadata = entry.Metadata is null || entry.Metadata.Count == 0
                        ? string.Empty
                        : string.Join(
                            ", ",
                            entry.Metadata
                                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .Select(pair => $"{pair.Key}={pair.Value}"));

                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.Category,-16} | " +
                        $"{entry.EventType,-40} | " +
                        $"{entry.Outcome,-12} | " +
                        $"StepId={entry.CorrelationContext.StepId} | " +
                        $"StepKey={entry.CorrelationContext.StepKey} | " +
                        $"Worker={entry.CorrelationContext.WorkerId} | " +
                        $"Reason={entry.Reason ?? string.Empty} | " +
                        $"Metadata=[{metadata}]");
                }

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Execution,
                    AiEngineEvents.Execution.Created);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Claim,
                    AiEngineEvents.Claim.Acquired);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Step,
                    AiEngineEvents.Step.Started);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Step,
                    AiEngineEvents.Step.Completed);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retry,
                    AiEngineEvents.Retry.Evaluated);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retry,
                    AiEngineEvents.Retry.Scheduled);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evaluated);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.Started);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Finalization,
                    AiEngineEvents.Finalization.Completed);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Execution,
                    AiEngineEvents.Execution.Finalized);

                Assert.All(entries, entry =>
                {
                    Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
                    Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationContext.StepId));
                    Assert.False(string.IsNullOrWhiteSpace(entry.CorrelationContext.StepKey));
                    Assert.False(string.IsNullOrWhiteSpace(entry.EventType));
                });

                var record = await dagStore.GetRecordAsync(
                    executionId);

                Assert.NotNull(record);
                Assert.Equal(AiExecutionStatus.Completed, record!.Status);
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
        /// Runs a 100-step distributed chaos execution with retention compaction only.
        /// </summary>
        /// <remarks>
        /// This test validates that atomic retention compaction is really applied.
        /// It intentionally disables eviction policy so compaction cannot be overridden
        /// by eviction precedence.
        /// </remarks>
        [RedisFact]
        public async Task DistributedChaos_Should_Apply_Atomic_Compaction_When_Eviction_Is_Disabled()
        {
            var scenario = DistributedChaosScenario.Steps100CompactionOnly();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            compactionOnly = true
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                var executionId = handle.ExecutionId!;

                var state = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotNull(state);

                var completedSteps = state!.Steps
                     .Where(x => x.Value.Status == AiStepExecutionStatus.Completed)
                     .Select(x => new
                     {
                         Step = x.Key,
                         Status = x.Value.Status,
                         HasInlineSize = x.Value.InlinePayloadSizeBytes.HasValue,
                         InlineSize = x.Value.InlinePayloadSizeBytes,
                         HasResult = x.Value.Result is not null,
                         IsEvicted = x.Value.IsEvictedFromHotState
                     })
                     .ToArray();

                _output.WriteLine("");
                _output.WriteLine("COMPLETED STEPS INLINE SIZE DEBUG");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"CompletedSteps.Count={completedSteps.Length}");
                _output.WriteLine($"CompletedSteps.WithInlineSize.Count={completedSteps.Count(x => x.HasInlineSize)}");
                _output.WriteLine($"CompletedSteps.WithResult.Count={completedSteps.Count(x => x.HasResult)}");
                _output.WriteLine($"CompletedSteps.Evicted.Count={completedSteps.Count(x => x.IsEvicted)}");

                foreach (var step in completedSteps.Take(20))
                {
                    _output.WriteLine(
                        $"{step.Step} | Status={step.Status} | HasInlineSize={step.HasInlineSize} | InlineSize={step.InlineSize} | HasResult={step.HasResult} | IsEvicted={step.IsEvicted}");
                }

                var compactedShells = state!.Steps
                    .Where(x =>
                        x.Value.Status == AiStepExecutionStatus.Completed &&
                        x.Value.Result is null &&
                        x.Value.InlinePayloadSizeBytes.HasValue)
                    .Select(x => new
                    {
                        Step = x.Key,
                        x.Value.Status,
                        x.Value.InlinePayloadSizeBytes,
                        HasResult = x.Value.Result is not null,
                        x.Value.IsEvictedFromHotState
                    })
                    .ToArray();

                _output.WriteLine("");
                _output.WriteLine("COMPACTED SHELLS DEBUG");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"CompactedShells.Count={compactedShells.Length}");

                foreach (var step in compactedShells.Take(20))
                {
                    _output.WriteLine(
                        $"{step.Step} | Status={step.Status} | InlineSize={step.InlinePayloadSizeBytes} | HasResult={step.HasResult} | IsEvicted={step.IsEvictedFromHotState}");
                }

                Assert.NotEmpty(compactedShells);

                Assert.All(
                    compactedShells,
                    step =>
                    {
                        Assert.False(step.HasResult);
                        Assert.False(step.IsEvictedFromHotState);
                    });

                // New compaction validation asserts.
                Assert.Equal(
                    scenario.StepCount,
                    completedSteps.Length);

                Assert.Equal(
                    scenario.StepCount,
                    compactedShells.Length);

                Assert.All(
                    completedSteps,
                    step =>
                    {
                        Assert.True(step.HasInlineSize);
                        Assert.Equal(0, step.InlineSize);
                        Assert.False(step.HasResult);
                        Assert.False(step.IsEvicted);
                    });

                Assert.All(
                    compactedShells,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.Equal(0, step.InlinePayloadSizeBytes);
                        Assert.False(step.HasResult);
                        Assert.False(step.IsEvictedFromHotState);
                    });

                await resolver.WarmAsync(
                    executionId,
                    state!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    state!,
                    resolver);

                var entries = await ledger.GetByExecutionAsync(
                    executionId);

                Assert.NotEmpty(entries);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evaluated);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Triggered);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Compacted);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Payload,
                    AiEngineEvents.Payload.Externalized);

                Assert.DoesNotContain(
                    entries,
                    entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Evicted);

                // New ledger validation asserts.
                var compactedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Compacted)
                    .ToArray();

                var externalizedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Payload &&
                        entry.EventType == AiEngineEvents.Payload.Externalized)
                    .ToArray();

                Assert.NotEmpty(compactedEvents);
                Assert.NotEmpty(externalizedEvents);

                Assert.All(
                    compactedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Applied, entry.Outcome));

                Assert.All(
                    externalizedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Persisted, entry.Outcome));

                Assert.DoesNotContain(
                    entries,
                    entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Evicted);

                _output.WriteLine("");
                _output.WriteLine("ATOMIC COMPACTION LEDGER SUMMARY");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in entries
                    .GroupBy(entry => new
                    {
                        entry.Category,
                        entry.EventType,
                        entry.Outcome
                    })
                    .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                    .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome.ToString(), StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.EventType,-40} | {group.Key.Outcome,-12} | Count={group.Count()}");
                }
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
        /// Runs a 100-step distributed chaos execution with retention eviction only.
        /// </summary>
        /// <remarks>
        /// This test validates that atomic retention eviction is really applied.
        /// It intentionally disables compaction policy so eviction can be validated in isolation.
        /// </remarks>
        [RedisFact]
        public async Task DistributedChaos_Should_Apply_Atomic_Eviction_When_Compaction_Is_Disabled()
        {
            var scenario = DistributedChaosScenario.Steps100EvictionOnly();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            evictionOnly = true
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var state = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(state);

                var completedSteps = state!.Steps
                    .Where(x => x.Value.Status == AiStepExecutionStatus.Completed)
                    .Select(x => new
                    {
                        Step = x.Key,
                        Status = x.Value.Status,
                        HasInlineSize = x.Value.InlinePayloadSizeBytes.HasValue,
                        InlineSize = x.Value.InlinePayloadSizeBytes,
                        HasResult = x.Value.Result is not null,
                        IsEvicted = x.Value.IsEvictedFromHotState
                    })
                    .ToArray();

                var evictedShells = completedSteps
                    .Where(x => x.IsEvicted)
                    .ToArray();

                _output.WriteLine("");
                _output.WriteLine("EVICTED SHELLS DEBUG");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"CompletedSteps.Count={completedSteps.Length}");
                _output.WriteLine($"EvictedShells.Count={evictedShells.Length}");
                _output.WriteLine($"CompletedSteps.WithResult.Count={completedSteps.Count(x => x.HasResult)}");
                _output.WriteLine($"CompletedSteps.WithInlineSize.Count={completedSteps.Count(x => x.HasInlineSize)}");

                foreach (var step in evictedShells.Take(20))
                {
                    _output.WriteLine(
                        $"{step.Step} | Status={step.Status} | InlineSize={step.InlineSize} | HasResult={step.HasResult} | IsEvicted={step.IsEvicted}");
                }

                Assert.Equal(
                    scenario.StepCount,
                    completedSteps.Length);

                Assert.NotEmpty(
                    evictedShells);

                Assert.All(
                    evictedShells,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.False(step.HasResult);
                        Assert.True(step.IsEvicted);
                        Assert.True(step.HasInlineSize);
                        Assert.Equal(0, step.InlineSize);
                    });

                await resolver.WarmAsync(
                    executionId,
                    state!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    state!,
                    resolver);

                var entries = await ledger.GetByExecutionAsync(
                    executionId);

                Assert.NotEmpty(entries);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evaluated);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Triggered);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evicted);

                Assert.DoesNotContain(
                    entries,
                    entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Compacted);

                var evictedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Evicted)
                    .ToArray();

                Assert.NotEmpty(evictedEvents);

                Assert.All(
                    evictedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Applied, entry.Outcome));

                _output.WriteLine("");
                _output.WriteLine("ATOMIC EVICTION LEDGER SUMMARY");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in entries
                    .GroupBy(entry => new
                    {
                        entry.Category,
                        entry.EventType,
                        entry.Outcome
                    })
                    .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                    .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome.ToString(), StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.EventType,-40} | {group.Key.Outcome,-12} | Count={group.Count()}");
                }
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
        /// Runs a 100-step distributed chaos execution with both atomic compaction and eviction enabled.
        /// </summary>
        /// <remarks>
        /// This test validates the hybrid retention path where compaction and eviction policies
        /// are both active. Eviction has precedence when the same step is selected for both actions,
        /// while compaction can still apply to other terminal payload-heavy steps.
        /// </remarks>
        [RedisFact]
        public async Task DistributedChaos_Should_Apply_Atomic_Compaction_And_Eviction_Together()
        {
            var scenario = DistributedChaosScenario.Steps100HybridRetention();

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            hybridRetention = true
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var state = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(state);

                var completedSteps = state!.Steps
                    .Where(x => x.Value.Status == AiStepExecutionStatus.Completed)
                    .Select(x => new
                    {
                        Step = x.Key,
                        Status = x.Value.Status,
                        HasInlineSize = x.Value.InlinePayloadSizeBytes.HasValue,
                        InlineSize = x.Value.InlinePayloadSizeBytes,
                        HasResult = x.Value.Result is not null,
                        IsEvicted = x.Value.IsEvictedFromHotState
                    })
                    .ToArray();

                var evictedShells = completedSteps
                    .Where(x => x.IsEvicted)
                    .ToArray();

                var compactedNonEvictedShells = completedSteps
                    .Where(x =>
                        !x.IsEvicted &&
                        !x.HasResult &&
                        x.HasInlineSize &&
                        x.InlineSize == 0)
                    .ToArray();

                _output.WriteLine("");
                _output.WriteLine("HYBRID RETENTION STATE DEBUG");
                _output.WriteLine("------------------------------------------------------------");
                _output.WriteLine($"CompletedSteps.Count={completedSteps.Length}");
                _output.WriteLine($"EvictedShells.Count={evictedShells.Length}");
                _output.WriteLine($"CompactedNonEvictedShells.Count={compactedNonEvictedShells.Length}");
                _output.WriteLine($"CompletedSteps.WithResult.Count={completedSteps.Count(x => x.HasResult)}");
                _output.WriteLine($"CompletedSteps.WithInlineSize.Count={completedSteps.Count(x => x.HasInlineSize)}");

                foreach (var step in completedSteps.Take(20))
                {
                    _output.WriteLine(
                        $"{step.Step} | Status={step.Status} | InlineSize={step.InlineSize} | HasResult={step.HasResult} | IsEvicted={step.IsEvicted}");
                }

                Assert.Equal(
                    scenario.StepCount,
                    completedSteps.Length);

                Assert.NotEmpty(
                    evictedShells);

                Assert.All(
                    evictedShells,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.False(step.HasResult);
                        Assert.True(step.IsEvicted);
                        Assert.True(step.HasInlineSize);
                        Assert.Equal(0, step.InlineSize);
                    });

                Assert.All(
                    compactedNonEvictedShells,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.False(step.HasResult);
                        Assert.False(step.IsEvicted);
                        Assert.True(step.HasInlineSize);
                        Assert.Equal(0, step.InlineSize);
                    });

                await resolver.WarmAsync(
                    executionId,
                    state!,
                    CancellationToken.None);

                await AssertRequiredStepsResolvedAsync(
                    scenario,
                    executionId,
                    state!,
                    resolver);

                var entries = await ledger.GetByExecutionAsync(
                    executionId);

                Assert.NotEmpty(entries);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evaluated);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Triggered);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Compacted);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Retention,
                    AiEngineEvents.Retention.Evicted);

                AssertLedgerContains(
                    entries,
                    AiDecisionLedgerCategory.Payload,
                    AiEngineEvents.Payload.Externalized);

                var compactedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Compacted)
                    .ToArray();

                var evictedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Retention &&
                        entry.EventType == AiEngineEvents.Retention.Evicted)
                    .ToArray();

                var externalizedEvents = entries
                    .Where(entry =>
                        entry.Category == AiDecisionLedgerCategory.Payload &&
                        entry.EventType == AiEngineEvents.Payload.Externalized)
                    .ToArray();

                Assert.NotEmpty(compactedEvents);
                Assert.NotEmpty(evictedEvents);
                Assert.NotEmpty(externalizedEvents);

                Assert.All(
                    compactedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Applied, entry.Outcome));

                Assert.All(
                    evictedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Applied, entry.Outcome));

                Assert.All(
                    externalizedEvents,
                    entry => Assert.Equal(AiDecisionLedgerOutcome.Persisted, entry.Outcome));

                _output.WriteLine("");
                _output.WriteLine("ATOMIC HYBRID RETENTION LEDGER SUMMARY");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in entries
                    .GroupBy(entry => new
                    {
                        entry.Category,
                        entry.EventType,
                        entry.Outcome
                    })
                    .OrderBy(group => group.Key.Category.ToString(), StringComparer.Ordinal)
                    .ThenBy(group => group.Key.EventType, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Outcome.ToString(), StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.EventType,-40} | {group.Key.Outcome,-12} | Count={group.Count()}");
                }
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
        /// Runs the distributed chaos scenario end-to-end.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        private async Task RunDistributedChaosScenarioAsync(
            DistributedChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            await using var host = await CreateDistributedChaosHostAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();
            var replayService = host.ServiceProvider.GetRequiredService<IAiExecutionReplayService>();
            var snapshotStore = host.ServiceProvider.GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var finalizedHook = host.ServiceProvider.GetRequiredService<DistributedChaosRunFinalizedHook>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true
                        }));

                Assert.NotNull(handle);

                AssertHandleAcceptedAfterEnqueue(
                    handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);

                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);
                Assert.Equal(handle.ExecutionId, final.ExecutionId);

                var executionId = handle.ExecutionId!;

                var finalized = await finalizedHook.WaitAsync(
                    scenario.SnapshotWaitTimeout);

                Assert.Equal(
                    executionId,
                    finalized.ExecutionId);

                var persistedRecordAfterFinal = await dagStore.GetRecordAsync(
                    executionId);

                _output.WriteLine(
                    $"FinalStatus='{final.Status}', FinalCompletedSteps='{final.CompletedSteps.Count}', " +
                    $"PersistedStatus='{persistedRecordAfterFinal?.Status}', PersistedCompletedSteps='{persistedRecordAfterFinal?.CompletedSteps.Count ?? -1}'.");

                Assert.NotNull(persistedRecordAfterFinal);

                Assert.Equal(
                    AiExecutionStatus.Completed,
                    persistedRecordAfterFinal!.Status);

                Assert.True(
                    persistedRecordAfterFinal.CompletedSteps.Count >= final.CompletedSteps.Count,
                    "Persisted completed-step history must never shrink compared to the returned terminal record.");

                Assert.Equal(
                    scenario.StepCount,
                    persistedRecordAfterFinal.CompletedSteps.Count);

                var snapshot = await snapshotStore.GetAsync(
                    executionId);

                Assert.NotNull(snapshot);

                Assert.Equal(
                    executionId,
                    snapshot!.ExecutionId);

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

                AssertRetentionDidNotBreakState(
                    scenario,
                    stateBeforeReplay!);

                AssertDistributedWorkerMetrics(
                    scenario,
                    metrics);

                var beforeReplayFingerprint = await CreateReplayFingerprintAsync(
                    scenario,
                    executionId,
                    recordBeforeReplay!,
                    stateBeforeReplay!,
                    resolver);

                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    executionId);

                Assert.Null(await dagStore.GetRecordAsync(executionId));
                Assert.Null(await dagStore.GetStateAsync(executionId));

                var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId
                    });

                Assert.NotNull(replayResult);
                Assert.True(replayResult.ReplayValid);
                Assert.True(replayResult.ExecutionFound);
                Assert.True(replayResult.SnapshotFound);

                var restoredRecord = await dagStore.GetRecordAsync(
                    executionId);

                var restoredState = await dagStore.GetStateAsync(
                    executionId);

                Assert.NotNull(restoredRecord);
                Assert.NotNull(restoredState);

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

                var afterReplayFingerprint = await CreateReplayFingerprintAsync(
                    scenario,
                    executionId,
                    restoredRecord!,
                    restoredState!,
                    resolver);

                Assert.Equal(beforeReplayFingerprint.Status, afterReplayFingerprint.Status);
                Assert.Equal(beforeReplayFingerprint.IsTerminal, afterReplayFingerprint.IsTerminal);
                Assert.Equal(beforeReplayFingerprint.CompletedSteps, afterReplayFingerprint.CompletedSteps);
                Assert.Equal(beforeReplayFingerprint.StepStatuses, afterReplayFingerprint.StepStatuses);
                Assert.Equal(beforeReplayFingerprint.RetryCounts, afterReplayFingerprint.RetryCounts);
                Assert.Equal(beforeReplayFingerprint.RequiredResolvedSteps, afterReplayFingerprint.RequiredResolvedSteps);

                _output.WriteLine(
                    $"Distributed chaos completed. ExecutionId='{executionId}', Steps='{scenario.StepCount}', Workers='{scenario.WorkerCount}'.");
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
        /// Validates execution-correlated controller-level run decision ledger events.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Record_Run_And_Queue_Ledger_Events()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostAsync(scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();

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
                         candidateId = scenario.CandidateId,
                         source = scenario.Name,
                         stepCount = scenario.StepCount,
                         workerCount = scenario.WorkerCount,
                         chaos = true,
                         ledger = true
                     },
                     source: scenario.Name));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);

                var entries = await ledger.GetByExecutionAsync(handle.ExecutionId!);

                Assert.NotEmpty(entries);

                AssertLedgerContains(entries, AiDecisionLedgerCategory.Run, AiEngineEvents.Run.Started);
                AssertLedgerContains(entries, AiDecisionLedgerCategory.Run, AiEngineEvents.Run.Completed);

                Assert.DoesNotContain(
                    entries,
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        (x.EventType == AiEngineEvents.Run.Queued || x.EventType == AiEngineEvents.Run.Dequeued));

                var started = entries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        x.EventType == AiEngineEvents.Run.Started);

                var completed = entries.Single(
                    x =>
                        x.Category == AiDecisionLedgerCategory.Run &&
                        x.EventType == AiEngineEvents.Run.Completed);

                Assert.Equal(AiDecisionLedgerOutcome.Started, started.Outcome);
                Assert.Equal(AiDecisionLedgerOutcome.Completed, completed.Outcome);

                Assert.Equal(handle.ExecutionId, started.CorrelationContext.ExecutionId);
                Assert.Equal(handle.ExecutionId, completed.CorrelationContext.ExecutionId);

                Assert.Equal(handle.RunId, started.CorrelationContext.CorrelationId);
                Assert.Equal(handle.RunId, completed.CorrelationContext.CorrelationId);

                Assert.NotNull(started.Metadata);
                Assert.NotNull(completed.Metadata);

                Assert.Equal(handle.RunId, started.Metadata!["run.id"]);
                Assert.Equal(handle.RunId, completed.Metadata!["run.id"]);

                Assert.Equal(handle.ExecutionId, started.Metadata!["execution.id"]);
                Assert.Equal(handle.ExecutionId, completed.Metadata!["execution.id"]);

                Assert.Equal(scenario.PipelineName, started.Metadata!["pipeline.name"]);
                Assert.Equal(scenario.PipelineName, completed.Metadata!["pipeline.name"]);

                _output.WriteLine("");
                _output.WriteLine("EXECUTION-CORRELATED RUN LEDGER EVENTS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var entry in entries
                    .Where(x => x.Category == AiDecisionLedgerCategory.Run)
                    .OrderBy(x => x.TimestampUtc))
                {
                    _output.WriteLine(
                        $"{entry.TimestampUtc:O} | " +
                        $"{entry.Category,-12} | " +
                        $"{entry.EventType,-30} | " +
                        $"{entry.Outcome,-12} | " +
                        $"ExecutionId={entry.CorrelationContext.ExecutionId} | " +
                        $"CorrelationId={entry.CorrelationContext.CorrelationId} | " +
                        $"Worker={entry.CorrelationContext.WorkerId}");
                }
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
        /// Validates execution control and human-input decision ledger events.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Record_Control_And_HumanInput_Ledger_Events()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostAsync(scenario);

            var controlService = host.ServiceProvider.GetRequiredService<IAiExecutionControlService>();
            var ledger = host.ServiceProvider.GetRequiredService<IAiDecisionLedger>();

            var executionId = Guid.NewGuid().ToString("N");

            await controlService.PauseExecutionAsync(
                executionId,
                reason: "test-pause",
                requestedBy: "integration-test");

            await controlService.MarkPausedAsync(
                executionId,
                requestedBy: "integration-test");

            await controlService.ResumeExecutionAsync(
                executionId,
                requestedBy: "integration-test");

            await controlService.MarkRunningAsync(
                executionId,
                requestedBy: "integration-test");

            await controlService.MarkWaitingForInputAsync(
                executionId,
                waitingKey: "approval-required",
                waitingStepName: "human-approval-step",
                reason: "approval required",
                requestedBy: "integration-test");

            await controlService.SubmitHumanInputAsync(
                executionId,
                waitingKey: "approval-required",
                input: new Dictionary<string, object?>
                {
                    ["approved"] = true,
                    ["comment"] = "approved by integration test"
                },
                submittedBy: "integration-test");

            var entries = await ledger.GetByExecutionAsync(executionId);

            Assert.NotEmpty(entries);

            AssertLedgerContains(entries, AiDecisionLedgerCategory.Control, AiEngineEvents.Control.PauseRequested);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.Control, AiEngineEvents.Control.Paused);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.Control, AiEngineEvents.Control.ResumeRequested);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.Control, AiEngineEvents.Control.Resumed);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.Control, AiEngineEvents.Control.StateChanged);

            AssertLedgerContains(entries, AiDecisionLedgerCategory.HumanInput, AiEngineEvents.HumanInput.Requested);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.HumanInput, AiEngineEvents.HumanInput.Waiting);
            AssertLedgerContains(entries, AiDecisionLedgerCategory.HumanInput, AiEngineEvents.HumanInput.Submitted);

            Assert.All(
                entries.Where(x =>
                    x.Category == AiDecisionLedgerCategory.Control ||
                    x.Category == AiDecisionLedgerCategory.HumanInput),
                entry =>
                {
                    Assert.Equal(executionId, entry.CorrelationContext.ExecutionId);
                    Assert.Equal("integration-test", entry.CorrelationContext.WorkerId);
                    Assert.False(string.IsNullOrWhiteSpace(entry.EventType));
                    Assert.NotNull(entry.Metadata);
                });

            _output.WriteLine("");
            _output.WriteLine("CONTROL / HUMAN INPUT LEDGER EVENTS");
            _output.WriteLine("------------------------------------------------------------");

            foreach (var entry in entries
                .Where(x =>
                    x.Category == AiDecisionLedgerCategory.Control ||
                    x.Category == AiDecisionLedgerCategory.HumanInput)
                .OrderBy(x => x.TimestampUtc)
                .ThenBy(x => x.EventType, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"{entry.TimestampUtc:O} | " +
                    $"{entry.Category,-16} | " +
                    $"{entry.EventType,-35} | " +
                    $"{entry.Outcome,-12} | " +
                    $"StepId={entry.CorrelationContext.StepId} | " +
                    $"StepKey={entry.CorrelationContext.StepKey} | " +
                    $"Worker={entry.CorrelationContext.WorkerId} | " +
                    $"Reason={entry.Reason}");
            }
        }

        /// <summary>
        /// Runs a distributed chaos execution with tracing configured in MemoryAndMongo mode
        /// and prints the execution-correlated trace timeline.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Print_Correlated_Traces_With_Memory_And_Mongo()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostWithMemoryAndMongoObservabilityAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var timeline = host.ServiceProvider.GetRequiredService<IAiTraceTimeline>();
            var mongoTraceStore = host.ServiceProvider.GetRequiredService<MongoAiRuntimeTraceStore>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            tracing = true,
                            tracingMode = "MemoryAndMongo"
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var executionId = handle.ExecutionId!;

                var memoryEvents = timeline
                    .Get(executionId)
                    .Concat(timeline.Get(handle.RunId))
                    .GroupBy(traceEvent => new
                    {
                        traceEvent.TimestampUtc,
                        traceEvent.Category,
                        traceEvent.Name,
                        traceEvent.StepId
                    })
                    .Select(group => group.First())
                    .OrderBy(traceEvent => traceEvent.TimestampUtc)
                    .ToArray();

                var mongoRecords = await WaitForMongoTraceRecordsAsync(
                    mongoTraceStore,
                    executionId,
                    handle.RunId,
                    scenario.Timeout);

                if (memoryEvents.Length == 0 && mongoRecords.Count == 0)
                {
                    _output.WriteLine("");
                    _output.WriteLine("TRACE DEBUG - NO TRACE EVENTS FOUND");
                    _output.WriteLine("------------------------------------------------------------");
                    _output.WriteLine($"RunId:       {handle.RunId}");
                    _output.WriteLine($"ExecutionId: {executionId}");
                    _output.WriteLine("Timeline execution lookup count: 0");
                    _output.WriteLine("Timeline run lookup count:       0");
                    _output.WriteLine("Mongo trace record count:        0");
                }

                Assert.True(
                    memoryEvents.Length > 0 || mongoRecords.Count > 0,
                    "Expected at least one trace event either in memory timeline or Mongo trace store.");

                _output.WriteLine("TRACE SUMMARY BY CATEGORY / NAME");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var group in memoryEvents
                    .GroupBy(traceEvent => new
                    {
                        traceEvent.Category,
                        traceEvent.Name
                    })
                    .OrderBy(group => group.Key.Category, StringComparer.Ordinal)
                    .ThenBy(group => group.Key.Name, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"{group.Key.Category,-16} | {group.Key.Name,-35} | Count={group.Count()}");
                }

                _output.WriteLine("");
                _output.WriteLine("TRACE TIMELINE");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var traceEvent in memoryEvents
                    .OrderBy(traceEvent => traceEvent.TimestampUtc)
                    .ThenBy(traceEvent => traceEvent.Category, StringComparer.Ordinal)
                    .ThenBy(traceEvent => traceEvent.Name, StringComparer.Ordinal))
                {
                    var runtime = traceEvent.Correlation?.Runtime;

                    var tags = traceEvent.Tags is null || traceEvent.Tags.Count == 0
                        ? string.Empty
                        : string.Join(
                            ", ",
                            traceEvent.Tags
                                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .Select(pair => $"{pair.Key}={pair.Value}"));

                    _output.WriteLine(
                        $"{traceEvent.TimestampUtc:O} | " +
                        $"{traceEvent.Category,-16} | " +
                        $"{traceEvent.Name,-35} | " +
                        $"ExecutionId={traceEvent.Correlation?.Runtime?.ExecutionId ?? string.Empty} | " +
                        $"RunId={traceEvent.Correlation?.Runtime?.RunId ?? string.Empty} | " +
                        $"CorrelationId={runtime?.CorrelationId ?? string.Empty} | " +
                        $"Pipeline={runtime?.PipelineName ?? string.Empty} | " +
                        $"PipelineKey={runtime?.PipelineKey ?? string.Empty} | " +
                        $"StepId={traceEvent.StepId ?? traceEvent.Correlation?.StepId ?? string.Empty} | " +
                        $"StepKey={traceEvent.Correlation?.StepKey ?? string.Empty} | " +
                        $"ClaimToken={traceEvent.Correlation?.ClaimToken ?? string.Empty} | " +
                        $"Worker={traceEvent.Correlation?.Runtime?.WorkerId ?? string.Empty} | " +
                        $"RuntimeInstance={runtime?.RuntimeInstanceId ?? string.Empty} | " +
                        $"Provider={traceEvent.Correlation?.Provider ?? string.Empty} | " +
                        $"Model={traceEvent.Correlation?.Model ?? string.Empty} | " +
                        $"Operation={traceEvent.Correlation?.Operation ?? string.Empty} | " +
                        $"Tags=[{tags}]");
                }

                Assert.Contains(
                    memoryEvents,
                    traceEvent =>
                        traceEvent.Correlation?.Runtime?.ExecutionId == executionId);

                Assert.Contains(
                    memoryEvents,
                    traceEvent =>
                        traceEvent.Correlation?.Runtime?.WorkerId is not null);

                Assert.Contains(
                    mongoRecords,
                    record =>
                        record.Correlation?.Runtime?.ExecutionId == executionId);
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
        /// Runs a distributed chaos execution with metrics configured in MemoryAndMongo mode
        /// and prints the runtime worker metric summary.
        /// </summary>
        [RedisFact]
        public async Task DistributedChaos_Should_Print_Runtime_Metrics_With_Memory_And_Mongo()
        {
            var scenario = DistributedChaosScenario.Steps100();

            await using var host = await CreateDistributedChaosHostWithMemoryAndMongoObservabilityAsync(
                scenario);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        scenario,
                        new
                        {
                            candidateId = scenario.CandidateId,
                            source = scenario.Name,
                            stepCount = scenario.StepCount,
                            workerCount = scenario.WorkerCount,
                            chaos = true,
                            metrics = true,
                            metricsMode = "MemoryAndMongo"
                        }));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    scenario.Timeout);

                Assert.NotNull(final);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                AssertDistributedWorkerMetrics(
                    scenario,
                    metrics);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();
                var terminalByStatus = metrics.Worker.GetTerminalByStatus();

                Assert.NotEmpty(workerCycles);
                Assert.NotEmpty(terminalByStatus);

                _output.WriteLine("");
                _output.WriteLine("============================================================");
                _output.WriteLine("RUNTIME METRICS - MEMORY + MONGO CONFIGURATION");
                _output.WriteLine("============================================================");
                _output.WriteLine($"RunId:       {handle.RunId}");
                _output.WriteLine($"ExecutionId: {handle.ExecutionId}");
                _output.WriteLine($"Pipeline:    {scenario.PipelineName}");
                _output.WriteLine($"Steps:       {scenario.StepCount}");
                _output.WriteLine($"Workers:     {scenario.WorkerCount}");
                _output.WriteLine("");

                _output.WriteLine("WORKER CYCLES");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var item in workerCycles
                    .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"WorkerId={item.Key,-45} | Cycles={item.Value}");
                }

                _output.WriteLine("");
                _output.WriteLine("TERMINAL STATUS METRICS");
                _output.WriteLine("------------------------------------------------------------");

                foreach (var item in terminalByStatus
                    .OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    _output.WriteLine(
                        $"Status={item.Key,-20} | Count={item.Value}");
                }

                Assert.True(
                    terminalByStatus.TryGetValue(
                        AiExecutionStatus.Completed.ToString(),
                        out var completedCount),
                    "No Completed terminal metric was recorded.");

                Assert.True(
                    completedCount > 0,
                    "Expected at least one Completed terminal metric.");
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
        /// Creates a runtime pipeline run request with a deterministic execution-context snapshot.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="input">The run input payload.</param>
        /// <returns>The runtime pipeline run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest(
            DistributedChaosScenario scenario,
            object input)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(input);

            return AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                pipelineName: scenario.PipelineName,
                pipelineDefinition: scenario.PipelineDefinition,
                input: input,
                source: scenario.Name);
        }

        /// <summary>
        /// Creates a distributed chaos host with tracing and metrics configured in
        /// MemoryAndMongo mode.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateDistributedChaosHostWithMemoryAndMongoObservabilityAsync(
            DistributedChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var options = CreateOptions(
                scenario);

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;
            options.Observability.EnableMetrics = true;

            options.Observability.Tracing.Mode = AiRuntimeTraceStoreMode.MemoryAndMongo;
            options.Observability.Tracing.MongoConnectionString = "mongodb://localhost:27017";
            options.Observability.Tracing.MongoDatabaseName = "multiplexed_ai_tests";
            options.Observability.Tracing.MongoCollectionName =
                $"ai_runtime_traces_distributed_chaos_{Guid.NewGuid():N}";

            options.Observability.Metrics.Mode = AiRuntimeMetricStoreMode.MemoryAndMongo;
            options.Observability.Metrics.MongoConnectionString = "mongodb://localhost:27017";
            options.Observability.Metrics.MongoDatabaseName = "multiplexed_ai_tests";
            options.Observability.Metrics.MongoCollectionName =
                $"ai_runtime_metrics_distributed_chaos_{Guid.NewGuid():N}";

            return await AiDagExecutionEngineFixture.CreateAsync(
                options,
                configureServices: services =>
                {
                    var finalizedHook = new DistributedChaosRunFinalizedHook();

                    services.AddSingleton(finalizedHook);
                    services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                        finalizedHook);

                    services.AddInMemoryAiDecisionLedger();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimeDistributedChaosIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Waits until MongoDB-backed trace records are visible for the execution or run.
        /// </summary>
        /// <param name="store">The Mongo trace store.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="runId">The run identifier.</param>
        /// <param name="timeout">The maximum wait time.</param>
        /// <returns>The trace records.</returns>
        private static async Task<IReadOnlyList<AiTraceRecord>> WaitForMongoTraceRecordsAsync(
            MongoAiRuntimeTraceStore store,
            string executionId,
            string runId,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            IReadOnlyList<AiTraceRecord> records = Array.Empty<AiTraceRecord>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                var executionRecords = await store.GetByExecutionAsync(
                    executionId);

                var runRecords = await store.GetByExecutionAsync(
                    runId);

                records = executionRecords
                    .Concat(runRecords)
                    .GroupBy(record => record.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(record => record.StartedAtUtc)
                    .ToArray();

                if (records.Count > 0)
                {
                    return records;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(100));
            }

            return records;
        }

        /// <summary>
        /// Creates a fully configured distributed chaos test host.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateDistributedChaosHostAsync(
            DistributedChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(scenario),
                configureServices: services =>
                {

                    var finalizedHook = new DistributedChaosRunFinalizedHook();

                    services.AddSingleton(finalizedHook);
                    services.AddSingleton<IAiRuntimePipelineRunLifecycleHook>(
                        finalizedHook);

                    services.AddInMemoryAiDecisionLedger();

                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimeDistributedChaosIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Creates runtime options for the distributed chaos scenario.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <returns>The configured engine options.</returns>
        private static AiEngineOptions CreateOptions(
            DistributedChaosScenario scenario)
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
                    MaxConcurrentRuns = 1,
                    QueueCapacity = 8,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false,
                    MaxLocalWorkersPerExecution = 5,
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = scenario.WorkerCount,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(60)
                    }
                }
            };

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;

            options.Observability.DecisionLedger.WriteMode = AiDecisionLedgerWriteMode.BestEffort;
            options.Observability.DecisionLedger.StorageMode = AiDecisionLedgerStorageMode.InMemory;

            options.Snapshots.Enabled = true;
            options.Snapshots.Mongo.Enabled = true;
            options.Snapshots.Mongo.ConnectionString = "mongodb://localhost:27017";
            options.Snapshots.Mongo.DatabaseName = "multiplexed_ai_tests";
            options.Snapshots.Mongo.CollectionName =
                $"execution_snapshots_distributed_chaos_{Guid.NewGuid():N}";

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressSnapshotIfExist = true;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        /// <summary>
        /// Asserts that the decision ledger contains at least one entry matching the
        /// specified category and event type.
        /// </summary>
        /// <param name="entries">
        /// The decision ledger entries recorded for the execution.
        /// </param>
        /// <param name="category">
        /// The expected decision ledger category.
        /// </param>
        /// <param name="eventType">
        /// The expected decision ledger event type.
        /// </param>
        private static void AssertLedgerContains(
            IReadOnlyCollection<AiDecisionLedgerEntry> entries,
            AiDecisionLedgerCategory category,
            string eventType)
        {
            Assert.Contains(entries, entry =>
                entry.Category == category &&
                entry.EventType == eventType);
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
                string.IsNullOrWhiteSpace(handle.RunId));

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
        /// Validates that required and retryable steps are resolvable.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="resolver">The step resolver.</param>
        private static async Task AssertRequiredStepsResolvedAsync(
            DistributedChaosScenario scenario,
            string executionId,
            AiExecutionState state,
            IAiExecutionStepResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(resolver);

            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);
                Assert.True(
                    step.Status == AiStepExecutionStatus.Completed,
                    $"Expected required step '{stepName}' to be Completed, but was '{step.Status}'. " +
                    $"InHotState='{state.Steps.ContainsKey(stepName)}', " +
                    $"HasResult='{step.Result is not null}', " +
                    $"RetryCount='{step.RetryState?.RetryCount ?? 0}'.");
                //Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);
            }

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);
                Assert.Equal(AiStepExecutionStatus.Completed, step!.Status);
                Assert.True(step.RetryState?.RetryCount >= 1);
            }
        }

        /// <summary>
        /// Validates that retained hot state does not contain stale claim ownership.
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
        /// Validates that retention did not corrupt the final execution state.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="state">The execution state.</param>
        private static void AssertRetentionDidNotBreakState(
            DistributedChaosScenario scenario,
            AiExecutionState state)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(state);

            Assert.NotNull(state.Steps);

            Assert.True(
                state.Steps.Count <= scenario.StepCount);

            Assert.All(
                state.Steps.Values,
                step =>
                {
                    Assert.True(
                        step.Status == AiStepExecutionStatus.Completed ||
                        step.Status == AiStepExecutionStatus.Failed);
                });
        }

        /// <summary>
        /// Validates distributed worker metrics.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="metrics">The metrics facade.</param>
        private void AssertDistributedWorkerMetrics(
            DistributedChaosScenario scenario,
            IAiRuntimeMetrics metrics)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            ArgumentNullException.ThrowIfNull(metrics);

            var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

            Assert.NotEmpty(workerCycles);

            Assert.True(
                workerCycles.Count >= scenario.MinimumExpectedParticipatingWorkers,
                $"Expected at least '{scenario.MinimumExpectedParticipatingWorkers}' distributed runtime workers to participate.");

            foreach (var item in workerCycles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                _output.WriteLine(
                    $"RuntimeInstanceId='{item.Key}', Cycles='{item.Value}'.");
            }
        }

        /// <summary>
        /// Deletes the live DAG execution bundle while preserving terminal snapshots.
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
        /// Creates a deterministic replay fingerprint.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="executionId">The execution identifier.</param>
        /// <param name="record">The execution record.</param>
        /// <param name="state">The execution state.</param>
        /// <param name="resolver">The step resolver.</param>
        /// <returns>The replay fingerprint.</returns>
        private static async Task<ReplayFingerprint> CreateReplayFingerprintAsync(
            DistributedChaosScenario scenario,
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

            var selectedSteps = scenario.FullStepFingerprint
                ? scenario.PipelineDefinition.Steps
                : scenario.PipelineDefinition.Steps.Where(step =>
                    !string.IsNullOrWhiteSpace(step.Name) &&
                    scenario.FingerprintStepNames.Contains(
                        step.Name,
                        StringComparer.Ordinal));

            var stepStatuses = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var step in selectedSteps)
            {
                if (string.IsNullOrWhiteSpace(step.Name))
                {
                    continue;
                }

                var status = await resolver.GetStepStatusAsync(
                    executionId,
                    step.Name,
                    state,
                    CancellationToken.None);

                Assert.NotNull(status);

                stepStatuses[step.Name] = status!.Status.ToString();
            }

            var retryCounts = new SortedDictionary<string, int>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.ExpectedRetriedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                retryCounts[stepName] =
                    step!.RetryState?.RetryCount ?? 0;
            }

            var required = new SortedDictionary<string, string>(
                StringComparer.Ordinal);

            foreach (var stepName in scenario.RequiredResolvedSteps)
            {
                var step = await resolver.GetStepAsync(
                    executionId,
                    stepName,
                    state,
                    CancellationToken.None);

                Assert.NotNull(step);

                required[stepName] = step!.Status.ToString();
            }

            return new ReplayFingerprint
            {
                Status = record.Status.ToString(),
                IsTerminal = record.IsTerminal,
                CompletedSteps = record.CompletedSteps
                    .OrderBy(step => step, StringComparer.Ordinal)
                    .ToArray(),
                StepStatuses = stepStatuses,
                RetryCounts = retryCounts,
                RequiredResolvedSteps = required
            };
        }

        /// <summary>
        /// Creates a parameterized pipeline definition for distributed chaos testing.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <returns>The pipeline definition.</returns>
        private static AiPipelineDefinition CreatePipelineDefinition(
            DistributedChaosScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "chaos-step-001",
                    StepKey = "hello-world",
                    Order = 1,
                    Config = CreateStepConfig(
                        scenario,
                        index: 1,
                        isFlaky: false)
                }
            };

            for (var index = 2; index < scenario.StepCount; index++)
            {
                var isFlaky =
                    index % scenario.FlakyStepInterval == 0;

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = $"chaos-step-{index:000}",
                        StepKey = isFlaky
                            ? "distributed.chaos.flaky-provider"
                            : "hello-world",
                        Order = index,
                        DependsOn = new[] { "chaos-step-001" },
                        Config = CreateStepConfig(
                            scenario,
                            index,
                            isFlaky)
                    });
            }

            steps.Add(
                new AiPipelineStepDefinition
                {
                    Name = "final-join-step",
                    StepKey = "hello-world",
                    Order = scenario.StepCount,
                    DependsOn = Enumerable.Range(2, scenario.StepCount - 2)
                        .Select(index => $"chaos-step-{index:000}")
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
                Name = scenario.PipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxDegreeOfParallelism"] = scenario.MaxDegreeOfParallelism,
                        ["maxProviderConcurrency"] = scenario.MaxProviderConcurrency,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 10,
                        ["jitter"] = false
                    },
                    ["retention"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["policies"] = scenario.RetentionPolicies.ToArray(),
                        ["archiveReason"] = scenario.RetentionArchiveReason,
                        ["trigger"] = new Dictionary<string, object?>
                        {
                            ["enabled"] = true,
                            ["maxStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxCompletedStepsInState"] = scenario.MaxCompletedStepsInState,
                            ["maxInlinePayloadBytes"] = scenario.MaxInlinePayloadBytes
                        }
                    }
                },
                Steps = steps
            };
        }

        /// <summary>
        /// Creates per-step configuration.
        /// </summary>
        /// <param name="scenario">The distributed chaos scenario.</param>
        /// <param name="index">The step index.</param>
        /// <param name="isFlaky">Whether the step should fail once before succeeding.</param>
        /// <returns>The step configuration.</returns>
        private static Dictionary<string, object?> CreateStepConfig(
            DistributedChaosScenario scenario,
            int index,
            bool isFlaky)
        {
            ArgumentNullException.ThrowIfNull(scenario);

            var config = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["delayMs"] = isFlaky ? 10 : 1
            };

            if (isFlaky)
            {
                config["attemptKey"] =
                    $"{scenario.PipelineName}:chaos-step-{index:000}";

                config["retry"] = new Dictionary<string, object?>
                {
                    ["maxRetries"] = 2,
                    ["strategy"] = "Fixed",
                    ["baseDelayMs"] = 15,
                    ["maxDelayMs"] = 15,
                    ["jitter"] = false
                };
            }

            return config;
        }


        /// <summary>
        /// Represents a configurable distributed chaos scenario.
        /// </summary>
        private sealed class DistributedChaosScenario
        {
            public required string Name { get; init; }

            public required string PipelineName { get; init; }

            public required string CandidateId { get; init; }

            public required string RetentionArchiveReason { get; init; }

            public AiPipelineDefinition PipelineDefinition { get; set; } = null!;

            public int StepCount { get; init; }

            public int WorkerCount { get; init; }

            public int MaxStepsPerCycle { get; init; }

            public int MaxWorkerCycles { get; init; }

            public int MaxDegreeOfParallelism { get; init; }

            public int MaxProviderConcurrency { get; init; }

            public int MaxCompletedStepsInState { get; init; }

            public int FlakyStepInterval { get; init; }

            public int MinimumExpectedParticipatingWorkers { get; init; }

            public bool FullStepFingerprint { get; init; } = true;

            public TimeSpan WorkerIdleDelay { get; init; }

            public TimeSpan Timeout { get; init; }

            public TimeSpan SnapshotWaitTimeout { get; init; } = TimeSpan.FromSeconds(60);

            public IReadOnlyCollection<string> RequiredResolvedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> ExpectedRetriedSteps { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> FingerprintStepNames { get; init; } =
                Array.Empty<string>();

            public IReadOnlyCollection<string> RetentionPolicies { get; init; } =
                new[]
                {
                    "retention.compact.terminal",
                    "retention.evict.terminal"
                };

            public int MaxInlinePayloadBytes { get; init; } = 1;

            /// <summary>
            /// Creates a 100-step distributed chaos scenario.
            /// </summary>
            /// <returns>The configured 100-step distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps100()
            {
                var pipelineName = $"distributed-chaos-100-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-090",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 98)
                    .Where(index => index % 9 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-100",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-100",
                    RetentionArchiveReason = "distributed-chaos-100-retention",
                    StepCount = 100,
                    WorkerCount = 10,
                    MaxStepsPerCycle = 1,
                    MaxWorkerCycles = 5000,
                    MaxDegreeOfParallelism = 12,
                    MaxProviderConcurrency = 3,
                    MaxCompletedStepsInState = 15,
                    FlakyStepInterval = 9,
                    MinimumExpectedParticipatingWorkers = 3,
                    FullStepFingerprint = true,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    MaxInlinePayloadBytes = 1,

                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    },
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            /// <summary>
            /// Creates a 100-step distributed chaos scenario dedicated to atomic retention eviction.
            /// </summary>
            /// <returns>The configured eviction-only distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps100EvictionOnly()
            {
                var pipelineName = $"distributed-chaos-100-eviction-only-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-090",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 98)
                    .Where(index => index % 9 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-100-eviction-only",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-100-eviction-only",
                    RetentionArchiveReason = "distributed-chaos-100-eviction-only-retention",
                    StepCount = 100,
                    WorkerCount = 10,
                    MaxStepsPerCycle = 1,
                    MaxWorkerCycles = 5000,
                    MaxDegreeOfParallelism = 12,
                    MaxProviderConcurrency = 3,

                    MaxCompletedStepsInState = 10,
                    MaxInlinePayloadBytes = 1,

                    RetentionPolicies = new[]
                    {
                        "retention.evict.terminal"
                    },

                    FlakyStepInterval = 9,
                    MinimumExpectedParticipatingWorkers = 3,
                    FullStepFingerprint = true,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            /// <summary>
            /// Creates a 100-step distributed chaos scenario dedicated to hybrid atomic retention.
            /// </summary>
            /// <returns>The configured hybrid compaction and eviction distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps100HybridRetention()
            {
                var pipelineName = $"distributed-chaos-100-hybrid-retention-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-090",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 98)
                    .Where(index => index % 9 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-100-hybrid-retention",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-100-hybrid-retention",
                    RetentionArchiveReason = "distributed-chaos-100-hybrid-retention",
                    StepCount = 100,
                    WorkerCount = 10,
                    MaxStepsPerCycle = 1,
                    MaxWorkerCycles = 5000,
                    MaxDegreeOfParallelism = 12,
                    MaxProviderConcurrency = 3,

                    // Creates eviction pressure while still allowing compaction to apply
                    // to terminal steps that exceed the inline payload threshold.
                    MaxCompletedStepsInState = 10,
                    MaxInlinePayloadBytes = 1,

                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal",
                        "retention.evict.terminal"
                    },

                    FlakyStepInterval = 9,
                    MinimumExpectedParticipatingWorkers = 3,
                    FullStepFingerprint = true,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            /// <summary>
            /// Creates a 500-step distributed chaos scenario.
            /// </summary>
            /// <returns>The configured 500-step distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps500()
            {
                var pipelineName = $"distributed-chaos-500-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-011",
                    "chaos-step-022",
                    "chaos-step-099",
                    "chaos-step-250",
                    "chaos-step-499",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 498)
                    .Where(index => index % 11 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-500",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-500",
                    RetentionArchiveReason = "distributed-chaos-500-retention",
                    StepCount = 500,
                    WorkerCount = 30,
                    MaxStepsPerCycle = 5,
                    MaxWorkerCycles = 10000,
                    MaxDegreeOfParallelism = 64,
                    MaxProviderConcurrency = 12,
                    MaxCompletedStepsInState = 50,
                    FlakyStepInterval = 11,
                    MinimumExpectedParticipatingWorkers = 5,
                    FullStepFingerprint = false,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromMinutes(10),
                    SnapshotWaitTimeout = TimeSpan.FromMinutes(3),
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            /// <summary>
            /// Creates a 100-step distributed chaos scenario dedicated to atomic retention compaction.
            /// </summary>
            /// <returns>The configured compaction-only distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps100CompactionOnly()
            {
                var pipelineName = $"distributed-chaos-100-compaction-only-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-009",
                    "chaos-step-018",
                    "chaos-step-090",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 98)
                    .Where(index => index % 9 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-100-compaction-only",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-100-compaction-only",
                    RetentionArchiveReason = "distributed-chaos-100-compaction-only-retention",
                    StepCount = 100,
                    WorkerCount = 10,
                    MaxStepsPerCycle = 1,
                    MaxWorkerCycles = 5000,
                    MaxDegreeOfParallelism = 12,
                    MaxProviderConcurrency = 3,

                    // Keep eviction pressure disabled for this scenario.
                    // The test must validate compaction only.
                    MaxCompletedStepsInState = 10000,

                    // Force inline payload threshold pressure.
                    MaxInlinePayloadBytes = 1,

                    RetentionPolicies = new[]
                    {
                        "retention.compact.terminal"
                    },

                    FlakyStepInterval = 9,
                    MinimumExpectedParticipatingWorkers = 3,
                    FullStepFingerprint = true,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromSeconds(180),
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }

            /// <summary>
            /// Creates a 500-step distributed chaos scenario with very aggressive retention.
            /// </summary>
            /// <returns>The configured aggressive-retention 500-step distributed chaos scenario.</returns>
            public static DistributedChaosScenario Steps500AggressiveRetention()
            {
                var pipelineName = $"distributed-chaos-500-aggressive-retention-{Guid.NewGuid():N}";

                var requiredSteps = new[]
                {
                    "chaos-step-001",
                    "chaos-step-011",
                    "chaos-step-022",
                    "chaos-step-099",
                    "chaos-step-250",
                    "chaos-step-499",
                    "final-join-step"
                };

                var retriedSteps = Enumerable.Range(2, 498)
                    .Where(index => index % 11 == 0)
                    .Select(index => $"chaos-step-{index:000}")
                    .ToArray();

                var scenario = new DistributedChaosScenario
                {
                    Name = "distributed-chaos-500-aggressive-retention",
                    PipelineName = pipelineName,
                    CandidateId = "candidate-distributed-chaos-500-aggressive-retention",
                    RetentionArchiveReason = "distributed-chaos-500-aggressive-retention",
                    StepCount = 500,
                    WorkerCount = 30,
                    MaxStepsPerCycle = 5,
                    MaxWorkerCycles = 12000,
                    MaxDegreeOfParallelism = 64,
                    MaxProviderConcurrency = 12,
                    MaxCompletedStepsInState = 10,
                    FlakyStepInterval = 11,
                    MinimumExpectedParticipatingWorkers = 5,
                    FullStepFingerprint = false,
                    WorkerIdleDelay = TimeSpan.FromMilliseconds(1),
                    Timeout = TimeSpan.FromMinutes(6),
                    SnapshotWaitTimeout = TimeSpan.FromMinutes(4),
                    RequiredResolvedSteps = requiredSteps,
                    ExpectedRetriedSteps = retriedSteps,
                    FingerprintStepNames = requiredSteps
                        .Concat(retriedSteps)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };

                scenario.PipelineDefinition = CreatePipelineDefinition(
                    scenario);

                return scenario;
            }
        }

        /// <summary>
        /// Stable comparable replay fingerprint.
        /// </summary>
        private sealed class ReplayFingerprint
        {
            public required string Status { get; init; }

            public required bool IsTerminal { get; init; }

            public required IReadOnlyList<string> CompletedSteps { get; init; }

            public required IReadOnlyDictionary<string, string> StepStatuses { get; init; }

            public required IReadOnlyDictionary<string, int> RetryCounts { get; init; }

            public required IReadOnlyDictionary<string, string> RequiredResolvedSteps { get; init; }
        }
    }

    public sealed class DistributedChaosRunFinalizedHook : IAiRuntimePipelineRunLifecycleHook
    {
        private readonly TaskCompletionSource<AiRuntimePipelineRunFinalizedContext> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Waits until a distributed chaos execution has been finalized.
        /// </summary>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The finalized run context.</returns>
        public async Task<AiRuntimePipelineRunFinalizedContext> WaitAsync(
            TimeSpan timeout)
        {
            return await _completion.Task.WaitAsync(timeout);
        }

        /// <inheritdoc />
        public Task OnFinalizedAsync(
            AiRuntimePipelineRunFinalizedContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            _completion.TrySetResult(context);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Distributed chaos test step that fails once per execution-specific attempt key and then succeeds.
    /// </summary>
    [AiStep("distributed.chaos.flaky-provider")]
    public sealed class DistributedChaosFlakyProviderStep : IAiStep
    {
        private static readonly ConcurrentDictionary<string, int> Attempts =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Name => "distributed.chaos.flaky-provider";

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
                    "ExecutionId is required for distributed chaos attempt tracking.");
            }

            var delayMs = await helper.GetConfigAsync<int?>(
                "delayMs",
                cancellationToken).ConfigureAwait(false) ?? 0;

            if (delayMs > 0)
            {
                await Task.Delay(
                    delayMs,
                    cancellationToken).ConfigureAwait(false);
            }

            var attemptKey = configuredAttemptKey + ":" + executionId;

            var attempt = Attempts.AddOrUpdate(
                attemptKey,
                1,
                (_, current) => current + 1);

            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    $"Intentional distributed chaos first-attempt failure for step '{attemptKey}'.");
            }

            return AiStepResult.Ok(
                output: $"Distributed chaos recovered after attempt {attempt}.",
                data: helper.ToDictionary(new
                {
                    attemptKey,
                    attempt,
                    executionId
                }));
        }
    }
}
