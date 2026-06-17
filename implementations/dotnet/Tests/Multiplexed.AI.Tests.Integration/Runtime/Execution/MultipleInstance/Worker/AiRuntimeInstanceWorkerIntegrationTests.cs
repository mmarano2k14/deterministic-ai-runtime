using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker
{
    /// <summary>
    /// Integration tests for runtime instance worker orchestration.
    /// </summary>
    [Collection("redis")]
    public sealed class AiRuntimeInstanceWorkerIntegrationTests
    {
        /// <summary>
        /// Verifies that the runtime instance worker can run a DAG execution until terminal
        /// without callers manually invoking the execution engine loop.
        /// </summary>
        [RedisFact]
        public async Task RuntimeInstanceWorker_Should_Run_Dag_Execution_Until_Terminal()
        {
            var pipelineName = $"runtime-instance-worker-{Guid.NewGuid():N}";
            var filePath = WriteWorkerPipelineDefinitionToConfig(pipelineName);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions($"{pipelineName}.json"));

            var worker = host.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();
            var workerIdentity = host.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();
            var metrics = host.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            var created = await host.Engine.CreateAsync(
                pipelineName,
                "worker-test");

            try
            {
                var final = await worker.RunExecutionAsync(
                    created.ExecutionId);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(4, final.CompletedSteps.Count);

                var cyclesByRuntimeInstance =
                    metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.True(
                    cyclesByRuntimeInstance.TryGetValue(
                        workerIdentity.WorkerId,
                        out var cycleCount),
                    $"No worker cycle metrics were recorded for worker id '{workerIdentity.WorkerId}'. " +
                    $"Available keys: '{string.Join(", ", cyclesByRuntimeInstance.Keys)}'.");

                Assert.True(
                    cycleCount > 0,
                    $"Expected at least one worker cycle for worker id '{workerIdentity.WorkerId}'.");

                var terminalByStatus =
                    metrics.Worker.GetTerminalByStatus();

                Assert.True(
                    terminalByStatus.TryGetValue(
                        AiExecutionStatus.Completed.ToString(),
                        out var completedCount),
                    "No worker terminal metric was recorded for Completed status.");

                Assert.True(
                    completedCount > 0,
                    "Expected at least one worker terminal completion metric.");
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    host.ServiceProvider,
                    created.ExecutionId);

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Verifies that multiple runtime instance workers can process the same DAG execution
        /// concurrently through the worker API until the execution reaches a terminal state.
        /// </summary>
        [RedisFact]
        public async Task RuntimeInstanceWorkers_Should_Run_Same_Dag_Execution_Concurrently_Until_Terminal()
        {
            var pipelineName = $"runtime-instance-worker-group-{Guid.NewGuid():N}";
            var filePath = WriteWorkerPipelineDefinitionToConfig(pipelineName);

            await using var hostA = await CreateWorkerHostAsync(
                "runtime-worker-a",
                $"{pipelineName}.json");

            await using var hostB = await CreateWorkerHostAsync(
                "runtime-worker-b",
                $"{pipelineName}.json");

            await using var hostC = await CreateWorkerHostAsync(
                "runtime-worker-c",
                $"{pipelineName}.json");

            var workerA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();
            var workerB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();
            var workerC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();

            var workerIdentityA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();
            var workerIdentityB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();
            var workerIdentityC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();

            var metricsA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var metricsB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var metricsC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            var created = await hostA.Engine.CreateAsync(
                pipelineName,
                "multi-worker-api-test");

            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(30));

                var workerTasks = new[]
                {
            workerA.RunExecutionAsync(created.ExecutionId, timeout.Token),
            workerB.RunExecutionAsync(created.ExecutionId, timeout.Token),
            workerC.RunExecutionAsync(created.ExecutionId, timeout.Token)
        };

                var completedTask = await Task.WhenAny(workerTasks);

                var final = await completedTask;

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(4, final.CompletedSteps.Count);

                var dagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                var record = await dagStore.GetRecordAsync(
                    created.ExecutionId);

                Assert.NotNull(record);
                Assert.True(record!.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, record.Status);

                var state = await dagStore.GetStateAsync(
                    created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(4, state!.Steps.Count);

                Assert.All(
                    state.Steps.Values,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.Null(step.ClaimedBy);
                        Assert.Null(step.ClaimToken);
                        Assert.Null(step.ClaimedAtUtc);
                        Assert.Null(step.LeaseExpiresAtUtc);
                    });

                AssertWorkerRecordedCycles(
                    metricsA,
                    workerIdentityA.WorkerId);

                AssertWorkerRecordedCycles(
                    metricsB,
                    workerIdentityB.WorkerId);

                AssertWorkerRecordedCycles(
                    metricsC,
                    workerIdentityC.WorkerId);
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    hostA.ServiceProvider,
                    created.ExecutionId);

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Verifies that the runtime instance worker group can coordinate multiple runtime
        /// instance workers against the same DAG execution until a terminal state is observed.
        /// </summary>
        [RedisFact]
        public async Task RuntimeInstanceWorkerGroup_Should_Run_Same_Dag_Execution_Until_Terminal()
        {
            var pipelineName = $"runtime-instance-worker-group-api-{Guid.NewGuid():N}";
            var filePath = WriteWorkerPipelineDefinitionToConfig(pipelineName);

            await using var hostA = await CreateWorkerHostAsync(
                "runtime-worker-group-a",
                $"{pipelineName}.json");

            await using var hostB = await CreateWorkerHostAsync(
                "runtime-worker-group-b",
                $"{pipelineName}.json");

            await using var hostC = await CreateWorkerHostAsync(
                "runtime-worker-group-c",
                $"{pipelineName}.json");

            var workerA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();
            var workerB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();
            var workerC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorker>();

            var group = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerGroup>();

            var workerIdentityA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();
            var workerIdentityB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();
            var workerIdentityC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeInstanceWorkerIdentity>();

            var metricsA = hostA.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var metricsB = hostB.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();
            var metricsC = hostC.ServiceProvider.GetRequiredService<IAiRuntimeMetrics>();

            var created = await hostA.Engine.CreateAsync(
                pipelineName,
                "worker-group-api-test");

            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(30));

                var final = await group.RunExecutionAsync(
                    created.ExecutionId,
                    new[]
                    {
                workerA,
                workerB,
                workerC
                    },
                    timeout.Token);

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(4, final.CompletedSteps.Count);

                var dagStore = hostA.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                var record = await dagStore.GetRecordAsync(created.ExecutionId);

                Assert.NotNull(record);
                Assert.True(record!.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, record.Status);

                var state = await dagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);
                Assert.Equal(4, state!.Steps.Count);

                Assert.All(
                    state.Steps.Values,
                    step =>
                    {
                        Assert.Equal(AiStepExecutionStatus.Completed, step.Status);
                        Assert.Null(step.ClaimedBy);
                        Assert.Null(step.ClaimToken);
                        Assert.Null(step.ClaimedAtUtc);
                        Assert.Null(step.LeaseExpiresAtUtc);
                    });

                AssertWorkerRecordedCycles(
                    metricsA,
                    workerIdentityA.WorkerId);

                AssertWorkerRecordedCycles(
                    metricsB,
                    workerIdentityB.WorkerId);

                AssertWorkerRecordedCycles(
                    metricsC,
                    workerIdentityC.WorkerId);

                var terminalByStatus =
                    metricsA.Worker.GetTerminalByStatus();

                Assert.True(
                    terminalByStatus.TryGetValue(
                        AiExecutionStatus.Completed.ToString(),
                        out var completedCount),
                    "No worker group terminal completion was observed through worker metrics.");

                Assert.True(
                    completedCount > 0,
                    "Expected at least one Completed terminal metric from the worker group run.");
            }
            finally
            {
                await CleanupDagExecutionAsync(
                    hostA.ServiceProvider,
                    created.ExecutionId);

                TryDeleteFile(filePath);
            }
        }

        /// <summary>
        /// Creates AI engine options using a JSON pipeline definition file.
        /// </summary>
        /// <param name="jsonFileName">The JSON pipeline definition file name under the config directory.</param>
        /// <returns>The configured AI engine options.</returns>
        private static AiEngineOptions CreateOptions(
            string jsonFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonFileName);

            return new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = "config/" + jsonFileName,
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = 2,
                    IdleDelay = TimeSpan.FromMilliseconds(10),
                    MaxCycles = 100,
                    IgnoreConcurrencyConflicts = true
                }
            };
        }

        /// <summary>
        /// Verifies that the background controller can enqueue one pipeline run request,
        /// create a distinct runtime execution, complete it, and keep the controller RunId
        /// separate from the runtime ExecutionId namespace.
        /// </summary>
        [RedisFact]
        public async Task RuntimePipelineBackgroundController_Should_Enqueue_Run_And_Complete_With_Different_RunId_And_ExecutionId()
        {
            var pipelineName = $"background-controller-single-{Guid.NewGuid():N}";
            var pipeline = CreateBackgroundControllerPipelineDefinition(pipelineName);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateRuntimePipelineControllerOptions());

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    CreateRunRequest(
                        pipelineName,
                        pipeline,
                        new
                        {
                            candidateId = "cand-001",
                            source = "background-controller-test"
                        },
                        source: "background-controller-test"));

                Assert.NotNull(handle);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.Equal(final.ExecutionId, handle.ExecutionId);

                Assert.False(
                    string.Equals(
                        handle.RunId,
                        handle.ExecutionId,
                        StringComparison.Ordinal),
                        "RunId and ExecutionId must be different. RunId belongs to the controller queue lifecycle; ExecutionId is the runtime namespace for the execution record and DAG state.");

                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, handle.Status);
                Assert.Equal(4, final.CompletedSteps.Count);
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
        /// Verifies that the background controller can enqueue multiple pipeline run requests
        /// using the same pipeline definition, create one distinct runtime execution per run,
        /// and keep every controller RunId separate from every runtime ExecutionId namespace.
        /// </summary>
        [RedisFact]
        public async Task RuntimePipelineBackgroundController_Should_Enqueue_Multiple_Runs_With_Distinct_RunIds_And_ExecutionIds()
        {
            var pipelineName = $"background-controller-multiple-{Guid.NewGuid():N}";
            var pipeline = CreateBackgroundControllerPipelineDefinition(pipelineName);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateRuntimePipelineControllerOptions());

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            await controller.StartAsync();

            var handles = new List<AiRuntimeWorkerRunHandle>();

            try
            {
                for (var index = 0; index < 5; index++)
                {
                    var handle = await controller.EnqueueAsync(
                        CreateRunRequest(
                            pipelineName,
                            pipeline,
                            new
                            {
                                candidateId = $"cand-{index:000}",
                                source = "background-controller-multi-test",
                                runIndex = index
                            },
                            source: "background-controller-multi-test"));

                    Assert.NotNull(handle);
                    Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                    Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);

                    handles.Add(handle);
                }

                var finals = await Task.WhenAll(
                    handles.Select(handle =>
                        handle.Completion.WaitAsync(TimeSpan.FromSeconds(30))));

                Assert.Equal(handles.Count, finals.Length);

                Assert.All(
                    finals,
                    final =>
                    {
                        Assert.NotNull(final);
                        Assert.True(final.IsTerminal);
                        Assert.Equal(AiExecutionStatus.Completed, final.Status);
                        Assert.Equal(4, final.CompletedSteps.Count);
                    });

                var runIds = handles
                    .Select(handle => handle.RunId)
                    .ToList();

                var executionIds = handles
                    .Select(handle => handle.ExecutionId)
                    .ToList();

                Assert.Equal(
                    handles.Count,
                    runIds.Distinct(StringComparer.Ordinal).Count());

                Assert.Equal(
                    handles.Count,
                    executionIds.Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                foreach (var handle in handles)
                {
                    Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                    Assert.Equal(AiRuntimeWorkerRunStatus.Completed, handle.Status);

                    Assert.False(
                        string.Equals(
                            handle.RunId,
                            handle.ExecutionId,
                            StringComparison.Ordinal),
                        "RunId and ExecutionId must be different. RunId belongs to the controller queue lifecycle; ExecutionId is the runtime namespace for the execution record and DAG state.");

                    var matchingFinal = finals.Single(final =>
                        string.Equals(
                            final.ExecutionId,
                            handle.ExecutionId,
                            StringComparison.Ordinal));

                    Assert.Equal(handle.ExecutionId, matchingFinal.ExecutionId);

                    var executionRecord = await dagStore.GetRecordAsync(
                        handle.ExecutionId!);

                    Assert.NotNull(executionRecord);
                    Assert.Equal(handle.ExecutionId, executionRecord!.ExecutionId);
                    Assert.Equal(pipelineName, executionRecord.PipelineName);

                    var executionState = await dagStore.GetStateAsync(
                        handle.ExecutionId!);

                    Assert.NotNull(executionState);
                    Assert.Equal(handle.ExecutionId, executionState!.ExecutionId);
                    Assert.Equal(pipelineName, executionState.PipelineName);
                    Assert.Equal(4, executionState.Steps.Count);

                    var runIdRecord = await dagStore.GetRecordAsync(
                        handle.RunId);

                    Assert.Null(runIdRecord);

                    var runIdState = await dagStore.GetStateAsync(
                        handle.RunId);

                    Assert.Null(runIdState);
                }
            }
            finally
            {
                await controller.StopAsync();

                foreach (var handle in handles)
                {
                    if (!string.IsNullOrWhiteSpace(handle.ExecutionId))
                    {
                        await CleanupDagExecutionAsync(
                            host.ServiceProvider,
                            handle.ExecutionId);
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that the background controller can run resilient 50-step DAG executions
        /// with retry, retention, and distributed concurrency configuration enabled, while
        /// keeping controller RunIds separate from runtime ExecutionIds and creating one
        /// distinct ExecutionId per submitted run.
        /// </summary>
        [RedisFact]
        public async Task RuntimePipelineBackgroundController_Should_Run_Resilient_50_Step_Dag_With_Retry_Retention_And_Concurrency()
        {
            var pipelineName = $"background-controller-resilience-{Guid.NewGuid():N}";
            var pipeline = CreateBackgroundControllerResiliencePipelineDefinition(pipelineName);

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateRuntimePipelineControllerOptions(),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimeInstanceWorkerIntegrationTests).Assembly);
                });

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
            var resolver = host.ServiceProvider.GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            var handles = new List<AiRuntimeWorkerRunHandle>();

            try
            {
                for (var index = 0; index < 2; index++)
                {
                    var handle = await controller.EnqueueAsync(
                        CreateRunRequest(
                            pipelineName,
                            pipeline,
                            new
                            {
                                candidateId = $"cand-resilience-{index:000}",
                                source = "background-controller-resilience-test",
                                runIndex = index
                            },
                            source: "background-controller-resilience-test"));

                    Assert.NotNull(handle);
                    Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                    Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);

                    handles.Add(handle);
                }

                var finals = await Task.WhenAll(
                    handles.Select(handle =>
                        handle.Completion.WaitAsync(TimeSpan.FromSeconds(90))));

                Assert.Equal(handles.Count, finals.Length);

                Assert.All(
                    finals,
                    final =>
                    {
                        Assert.NotNull(final);
                        Assert.True(final.IsTerminal);
                        Assert.Equal(AiExecutionStatus.Completed, final.Status);
                        Assert.Equal(50, final.CompletedSteps.Count);
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
                    });

                var executionIds = handles
                    .Select(handle => handle.ExecutionId!)
                    .ToList();

                // Every submitted controller run must have a distinct RunId.
                Assert.Equal(
                    handles.Count,
                    runIds.Distinct(StringComparer.Ordinal).Count());

                // Every created runtime execution must have a distinct ExecutionId.
                Assert.Equal(
                    handles.Count,
                    executionIds.Distinct(StringComparer.Ordinal).Count());

                // Controller RunIds and runtime ExecutionIds must live in separate identity spaces.
                Assert.Empty(
                    runIds.Intersect(
                        executionIds,
                        StringComparer.Ordinal));

                // Every created runtime execution must have a distinct ExecutionId.
                Assert.Equal(
                    handles.Count,
                    executionIds
                        .Where(executionId => !string.IsNullOrWhiteSpace(executionId))
                        .Distinct(StringComparer.Ordinal)
                        .Count());

                foreach (var handle in handles)
                {
                    Assert.False(string.IsNullOrWhiteSpace(handle.RunId));
                    Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                    Assert.Equal(AiRuntimeWorkerRunStatus.Completed, handle.Status);

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
                    Assert.Equal(50, matchingFinal.CompletedSteps.Count);

                    var executionRecord = await dagStore.GetRecordAsync(
                        handle.ExecutionId!);

                    Assert.NotNull(executionRecord);
                    Assert.Equal(handle.ExecutionId, executionRecord!.ExecutionId);
                    Assert.Equal(pipelineName, executionRecord.PipelineName);
                    Assert.Equal(AiExecutionStatus.Completed, executionRecord.Status);
                    Assert.Equal(50, executionRecord.CompletedSteps.Count);

                    var executionState = await dagStore.GetStateAsync(
                        handle.ExecutionId!);

                    Assert.NotNull(executionState);
                    Assert.Equal(handle.ExecutionId, executionState!.ExecutionId);
                    Assert.Equal(pipelineName, executionState.PipelineName);

                    // The controller RunId must never be used as a runtime execution namespace.
                    var runIdRecord = await dagStore.GetRecordAsync(
                        handle.RunId);

                    Assert.Null(runIdRecord);

                    var runIdState = await dagStore.GetStateAsync(
                        handle.RunId);

                    Assert.Null(runIdState);

                    await resolver.WarmAsync(
                        handle.ExecutionId!,
                        executionState);

                    var flakyStep = await resolver.GetStepAsync(
                        handle.ExecutionId!,
                        "flaky-provider-step",
                        executionState);

                    Assert.NotNull(flakyStep);
                    Assert.Equal(AiStepExecutionStatus.Completed, flakyStep!.Status);
                    Assert.NotNull(flakyStep.Retry);
                    Assert.True(flakyStep.RetryState?.RetryCount >= 1);

                    var finalStep = await resolver.GetStepAsync(
                        handle.ExecutionId!,
                        "final-join-step",
                        executionState);

                    Assert.NotNull(finalStep);
                    Assert.Equal(AiStepExecutionStatus.Completed, finalStep!.Status);

                    var hotCompletedSteps = executionState.Steps.Values
                        .Count(step => step.Status == AiStepExecutionStatus.Completed);

                    Assert.True(
                        hotCompletedSteps <= 50,
                        "Retention-enabled execution should remain valid even when completed steps are compacted or evicted from hot state.");

                    Assert.All(
                        executionState.Steps.Values,
                        step =>
                        {
                            Assert.Null(step.ClaimedBy);
                            Assert.Null(step.ClaimToken);
                            Assert.Null(step.ClaimedAtUtc);
                            Assert.Null(step.LeaseExpiresAtUtc);
                        });
                }
            }
            finally
            {
                await controller.StopAsync();

                foreach (var handle in handles)
                {
                    if (!string.IsNullOrWhiteSpace(handle.ExecutionId))
                    {
                        await CleanupDagExecutionAsync(
                            host.ServiceProvider,
                            handle.ExecutionId);
                    }
                }
            }
        }

        /// <summary>
        /// Creates a runtime pipeline run request with a deterministic execution context snapshot.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="pipelineDefinition">The pipeline definition.</param>
        /// <param name="input">The input payload.</param>
        /// <param name="source">The optional source value used for diagnostics.</param>
        /// <returns>The runtime pipeline run request.</returns>
        private static AiRuntimePipelineRunRequest CreateRunRequest(
            string pipelineName,
            AiPipelineDefinition pipelineDefinition,
            object input,
            string? source = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentNullException.ThrowIfNull(pipelineDefinition);
            ArgumentNullException.ThrowIfNull(input);

            return AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                pipelineName: pipelineName,
                pipelineDefinition: pipelineDefinition,
                input: input,
                source: source);
        }

        /// <summary>
        /// Creates AI engine options for runtime-published pipeline definitions.
        /// </summary>
        /// <returns>The configured AI engine options.</returns>
        private static AiEngineOptions CreateRuntimePipelineControllerOptions()
        {
            return new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Runtime",
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = 2,
                    IdleDelay = TimeSpan.FromMilliseconds(10),
                    MaxCycles = 100,
                    IgnoreConcurrencyConflicts = true
                },
                PipelineBackgroundController = new AiRuntimePipelineBackgroundControllerOptions
                {
                    MaxConcurrentRuns = 2,
                    QueueCapacity = 16,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false
                }
            };
        }

        /// <summary>
        /// Creates a deterministic DAG pipeline definition for background controller tests.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The pipeline definition.</returns>
        private static AiPipelineDefinition CreateBackgroundControllerPipelineDefinition(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "step-a",
                    StepKey = "hello-world",
                    Order = 1,
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = 10
                    }
                },
                new()
                {
                    Name = "step-b",
                    StepKey = "hello-world",
                    Order = 2,
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = 10
                    }
                },
                new()
                {
                    Name = "step-c",
                    StepKey = "hello-world",
                    Order = 3,
                    DependsOn = new[] { "step-a", "step-b" },
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = 10
                    }
                },
                new()
                {
                    Name = "step-d",
                    StepKey = "hello-world",
                    Order = 4,
                    DependsOn = new[] { "step-c" },
                    Config = new Dictionary<string, object?>
                    {
                        ["delayMs"] = 10
                    }
                }
            }
            };
        }

        /// <summary>
        /// Creates a fully wired worker integration host with a deterministic runtime instance identity.
        /// </summary>
        /// <param name="runtimeInstanceId">The deterministic runtime instance identifier.</param>
        /// <param name="jsonFileName">The JSON pipeline definition file name under the config directory.</param>
        /// <returns>The created worker integration host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateWorkerHostAsync(
            string runtimeInstanceId,
            string jsonFileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(jsonFileName);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(jsonFileName),
                configureServices: services =>
                {
                    services.RemoveAll<IAiRuntimeInstanceIdentityDescriptor>();
                    services.AddSingleton<IAiRuntimeInstanceIdentityDescriptor>(
                        new TestAiRuntimeInstanceIdentity(runtimeInstanceId));
                });
        }

        /// <summary>
        /// Verifies that worker cycle metrics were recorded for the expected runtime instance.
        /// </summary>
        /// <param name="metrics">The runtime metrics facade.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        private static void AssertWorkerRecordedCycles(
            IAiRuntimeMetrics metrics,
            string runtimeInstanceId)
        {
            ArgumentNullException.ThrowIfNull(metrics);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var cyclesByRuntimeInstance =
                metrics.Worker.GetCyclesByRuntimeInstance();

            Assert.True(
                cyclesByRuntimeInstance.TryGetValue(
                    runtimeInstanceId,
                    out var cycleCount),
                $"No worker cycle metrics were recorded for runtime instance '{runtimeInstanceId}'.");

            Assert.True(
                cycleCount > 0,
                $"Expected at least one worker cycle for runtime instance '{runtimeInstanceId}'.");
        }

        /// <summary>
        /// Writes a deterministic DAG pipeline definition for runtime instance worker tests.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The created file path.</returns>
        private static string WriteWorkerPipelineDefinitionToConfig(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var definition = new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
                {
                    new()
                    {
                        Name = "step-a",
                        StepKey = "hello-world",
                        Order = 1,
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-b",
                        StepKey = "hello-world",
                        Order = 2,
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-c",
                        StepKey = "hello-world",
                        Order = 3,
                        DependsOn = new[] { "step-a", "step-b" },
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    },
                    new()
                    {
                        Name = "step-d",
                        StepKey = "hello-world",
                        Order = 4,
                        DependsOn = new[] { "step-c" },
                        Config = new Dictionary<string, object?>
                        {
                            ["delayMs"] = 10
                        }
                    }
                }
            };

            var root = new
            {
                pipelines = new[]
                {
                    definition
                }
            };

            var json = JsonSerializer.Serialize(
                root,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            var baseDir = AppContext.BaseDirectory;
            var configDir = Path.Combine(baseDir, "config");

            Directory.CreateDirectory(configDir);

            var filePath = Path.Combine(configDir, $"{pipelineName}.json");

            File.WriteAllText(filePath, json);

            return filePath;
        }

        /// <summary>
        /// Creates a resilient 50-step DAG pipeline definition for background controller stress tests.
        /// </summary>
        /// <param name="pipelineName">The generated pipeline name.</param>
        /// <returns>The pipeline definition.</returns>
        private static AiPipelineDefinition CreateBackgroundControllerResiliencePipelineDefinition(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = "flaky-provider-step",
                    StepKey = "background.flaky-provider",
                    Order = 1,
                    Config = new Dictionary<string, object?>
                    {
                        ["attemptKey"] = pipelineName + ":flaky-provider-step",
                        ["provider"] = "openai",
                        ["model"] = "gpt-4.1",
                        ["operation"] = "llm.chat",
                        ["delayMs"] = 25,
                        ["retry"] = new Dictionary<string, object?>
                        {
                            ["maxRetries"] = 2,
                            ["strategy"] = "Fixed",
                            ["baseDelayMs"] = 50,
                            ["maxDelayMs"] = 50,
                            ["jitter"] = false
                        }
                    }
                }
            };

            for (var index = 1; index <= 48; index++)
            {
                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = $"provider-step-{index:00}",
                        StepKey = "hello-world",
                        Order = index + 1,
                        DependsOn = new[] { "flaky-provider-step" },
                        Config = new Dictionary<string, object?>
                        {
                            ["provider"] = "openai",
                            ["model"] = "gpt-4.1",
                            ["operation"] = "llm.chat",
                            ["delayMs"] = 15
                        }
                    });
            }

            steps.Add(
                new AiPipelineStepDefinition
                {
                    Name = "final-join-step",
                    StepKey = "hello-world",
                    Order = 50,
                    DependsOn = Enumerable.Range(1, 48)
                        .Select(index => $"provider-step-{index:00}")
                        .ToArray(),
                    Config = new Dictionary<string, object?>
                    {
                        ["provider"] = "openai",
                        ["model"] = "gpt-4.1",
                        ["operation"] = "llm.compose",
                        ["delayMs"] = 10
                    }
                });

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = new Dictionary<string, object?>
                {
                    ["concurrency"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["maxDegreeOfParallelism"] = 8,
                        ["maxProviderConcurrency"] = 2,
                        ["leaseSeconds"] = 60,
                        ["defaultRetryAfterMs"] = 25,
                        ["jitter"] = false
                    },
                    ["retention"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["mode"] = "Hybrid",
                        ["strategy"] = "Hybrid",
                        ["maxCompletedStepsInState"] = 10,
                        ["maxInlinePayloadBytes"] = 1
                    }
                },
                Steps = steps
            };
        }

        /// <summary>
        /// Deletes the distributed DAG execution bundle created by the test.
        /// </summary>
        /// <param name="serviceProvider">The service provider used to resolve the DAG store.</param>
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
        /// Deletes a file when it exists.
        /// </summary>
        /// <param name="filePath">The file path.</param>
        private static void TryDeleteFile(
            string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    /// <summary>
    /// Test step that fails on the first attempt for each execution-specific attempt key,
    /// then succeeds on subsequent attempts.
    /// </summary>
    [AiStep("background.flaky-provider")]
    public sealed class BackgroundControllerFlakyProviderStep : IAiStep
    {
        private static readonly ConcurrentDictionary<string, int> Attempts =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public string Name => "background.flaky-provider";

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
                    $"Intentional first-attempt failure for background controller step '{attemptKey}'.");
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