using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.MultipleInstance.Worker.Control
{
    /// <summary>
    /// Integration tests for controller-level queue pause and resume behavior.
    /// </summary>
    /// <remarks>
    /// These tests validate layer 1 control-plane behavior. Queue control affects
    /// queued runs before execution creation and is intentionally separate from
    /// execution-level control state such as pause, resume, cancellation, and
    /// waiting for human input.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiRuntimePipelineBackgroundControllerQueueControlTests
    {
        /// <summary>
        /// Verifies that pausing the controller queue prevents a queued run from starting
        /// until the queue is resumed.
        /// </summary>
        [RedisFact]
        public async Task PauseQueueAsync_WhenRunIsQueued_ShouldPreventRunFromStartingUntilResume()
        {
            var pipelineName = $"queue-control-pause-resume-{Guid.NewGuid():N}";

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = AiRuntimeWorkerRunHandle.CreateTestHandle();

            try
            {
                await controller.PauseQueueAsync(
                        reason: "integration test queue pause",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: pipelineName,
                        pipelineDefinition: CreatePipelineDefinition(pipelineName),
                        input: new
                        {
                            source = "queue-control-test"
                        },
                        source: "queue-control-test"))
                    .ConfigureAwait(false);

                Assert.NotNull(handle);
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);
                Assert.True(string.IsNullOrWhiteSpace(handle.ExecutionId));

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);
                Assert.True(string.IsNullOrWhiteSpace(handle.ExecutionId));

                await controller.ResumeQueueAsync(
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, handle.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Verifies that pausing the controller queue does not stop an already running execution.
        /// </summary>
        [RedisFact]
        public async Task PauseQueueAsync_WhenRunIsAlreadyRunning_ShouldAllowRunningExecutionToComplete()
        {
            var pipelineName = $"queue-control-running-{Guid.NewGuid():N}";
            var signalKey = $"queue-control-running-{Guid.NewGuid():N}";

            QueueControlDelayStepSignalRegistry.Create(signalKey);

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: pipelineName,
                        pipelineDefinition: CreatePipelineDefinition(
                            pipelineName,
                            signalKey,
                            delayMs: 300),
                        input: new
                        {
                            source = "queue-control-running-test"
                        },
                        source: "queue-control-running-test"))
                    .ConfigureAwait(false);

                Assert.NotNull(handle);

                await QueueControlDelayStepSignalRegistry.WaitForStartedAsync(
                    signalKey,
                    TimeSpan.FromSeconds(15));

                await controller.PauseQueueAsync(
                        reason: "pause after run has started",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, handle.Status);
                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }

                QueueControlDelayStepSignalRegistry.Remove(signalKey);
            }
        }

        /// <summary>
        /// Verifies that a queued run can be cancelled before execution creation starts.
        /// </summary>
        [RedisFact]
        public async Task CancelQueuedRunAsync_WhenRunIsStillQueued_ShouldCancelWithoutCreatingExecution()
        {
            var pipelineName = $"queue-control-cancel-queued-{Guid.NewGuid():N}";

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = AiRuntimeWorkerRunHandle.CreateTestHandle();

            try
            {
                await controller.PauseQueueAsync(
                        reason: "pause queue before cancelling queued run",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: pipelineName,
                        pipelineDefinition: CreatePipelineDefinition(pipelineName),
                        input: new
                        {
                            source = "queue-control-cancel-queued-test"
                        },
                        source: "queue-control-cancel-queued-test"))
                    .ConfigureAwait(false);

                Assert.NotNull(handle);
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, handle.Status);
                Assert.True(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var cancelled = await controller.CancelQueuedRunAsync(
                        handle.RunId,
                        reason: "cancel queued run from integration test",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                Assert.True(cancelled);
                Assert.Equal(AiRuntimeWorkerRunStatus.Cancelled, handle.Status);
                Assert.True(string.IsNullOrWhiteSpace(handle.ExecutionId));

                await controller.ResumeQueueAsync(
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(5));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Cancelled, final.Status);

                Assert.Equal(
                    handle.RunId,
                    final.ExecutionId);

                Assert.Equal(AiRuntimeWorkerRunStatus.Cancelled, handle.Status);
                Assert.True(string.IsNullOrWhiteSpace(handle.ExecutionId));
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Verifies that cancelling an unknown queued run returns false.
        /// </summary>
        [RedisFact]
        public async Task CancelQueuedRunAsync_WhenRunIdDoesNotExist_ShouldReturnFalse()
        {
            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();
            

            try
            {
                var cancelled = await controller.CancelQueuedRunAsync(
                        AiRuntimeWorkerRunHandle.CreateTestHandle().RunId,
                        reason: "missing run",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                Assert.False(cancelled);
            }
            finally
            {
                await controller.StopAsync();
            }
        }

        /// <summary>
        /// Verifies that cancelling a running controller run delegates cancellation to execution control.
        /// </summary>
        [RedisFact]
        public async Task CancelRunAsync_WhenRunIsRunning_ShouldCancelDurableExecution()
        {
            var pipelineName = $"queue-control-cancel-running-{Guid.NewGuid():N}";
            var signalKey = $"queue-control-cancel-running-{Guid.NewGuid():N}";

            QueueControlDelayStepSignalRegistry.Create(signalKey);

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: pipelineName,
                        pipelineDefinition: CreatePipelineDefinition(
                            pipelineName,
                            signalKey,
                            delayMs: 300),
                        input: new
                        {
                            source = "queue-control-cancel-running-test"
                        },
                        source: "queue-control-cancel-running-test"))
                    .ConfigureAwait(false);

                Assert.NotNull(handle);

                await QueueControlDelayStepSignalRegistry.WaitForStartedAsync(
                    signalKey,
                    TimeSpan.FromSeconds(15));

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var cancelled = await controller.CancelRunAsync(
                        handle.RunId,
                        reason: "cancel running run from integration test",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                Assert.True(cancelled);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Cancelled, final.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Cancelled, handle.Status);
                Assert.Equal(handle.ExecutionId, final.ExecutionId);

                var persistedRecord = await dagStore.GetRecordAsync(
                    handle.ExecutionId!);

                Assert.NotNull(persistedRecord);
                Assert.Equal(AiExecutionStatus.Cancelled, persistedRecord!.Status);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }

                QueueControlDelayStepSignalRegistry.Remove(signalKey);
            }
        }

        /// <summary>
        /// Verifies that a new run can be added while the controller is already processing another run.
        /// </summary>
        [RedisFact]
        public async Task EnqueueAsync_WhenControllerIsRunning_ShouldHotAddRunToQueue()
        {
            var firstPipelineName = $"queue-control-hot-add-first-{Guid.NewGuid():N}";
            var secondPipelineName = $"queue-control-hot-add-second-{Guid.NewGuid():N}";
            var firstSignalKey = $"queue-control-hot-add-first-{Guid.NewGuid():N}";

            QueueControlDelayStepSignalRegistry.Create(firstSignalKey);

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? firstHandle = null;
            AiRuntimeWorkerRunHandle? secondHandle = null;

            try
            {
                firstHandle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: firstPipelineName,
                        pipelineDefinition: CreatePipelineDefinition(
                            firstPipelineName,
                            firstSignalKey,
                            delayMs: 300),
                        input: new
                        {
                            source = "queue-control-hot-add-first"
                        },
                        source: "queue-control-hot-add-first"))
                    .ConfigureAwait(false);

                Assert.NotNull(firstHandle);

                await QueueControlDelayStepSignalRegistry.WaitForStartedAsync(
                    firstSignalKey,
                    TimeSpan.FromSeconds(15));

                secondHandle = await controller.EnqueueAsync(
                AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                    pipelineName: secondPipelineName,
                    pipelineDefinition: CreatePipelineDefinition(secondPipelineName),
                    input: new
                    {
                        source = "queue-control-hot-add-second"
                    },
                    source: "queue-control-hot-add-second"))
                .ConfigureAwait(false);

                Assert.NotNull(secondHandle);
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, secondHandle.Status);
                Assert.True(string.IsNullOrWhiteSpace(secondHandle.ExecutionId));

                var firstFinal = await firstHandle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                var secondFinal = await secondHandle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(firstFinal);
                Assert.NotNull(secondFinal);

                Assert.Equal(AiExecutionStatus.Completed, firstFinal.Status);
                Assert.Equal(AiExecutionStatus.Completed, secondFinal.Status);

                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, firstHandle.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, secondHandle.Status);

                Assert.False(string.IsNullOrWhiteSpace(firstHandle.ExecutionId));
                Assert.False(string.IsNullOrWhiteSpace(secondHandle.ExecutionId));
                Assert.NotEqual(firstHandle.ExecutionId, secondHandle.ExecutionId);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(firstHandle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        firstHandle.ExecutionId);
                }

                if (!string.IsNullOrWhiteSpace(secondHandle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        secondHandle.ExecutionId);
                }

                QueueControlDelayStepSignalRegistry.Remove(firstSignalKey);
            }
        }

        /// <summary>
        /// Verifies that runs can be added while the controller queue is paused and start after resume.
        /// </summary>
        [RedisFact]
        public async Task EnqueueAsync_WhenQueueIsPaused_ShouldHotAddRunAndStartAfterResume()
        {
            var firstPipelineName = $"queue-control-hot-add-paused-first-{Guid.NewGuid():N}";
            var secondPipelineName = $"queue-control-hot-add-paused-second-{Guid.NewGuid():N}";

            await using var host = await CreateHostAsync();

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? firstHandle = null;
            AiRuntimeWorkerRunHandle? secondHandle = null;

            try
            {
                await controller.PauseQueueAsync(
                        reason: "pause before hot adding queued runs",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                firstHandle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: firstPipelineName,
                        pipelineDefinition: CreatePipelineDefinition(firstPipelineName),
                        input: new
                        {
                            source = "queue-control-hot-add-paused-first"
                        },
                        source: "queue-control-hot-add-paused-first"))
                    .ConfigureAwait(false);

                secondHandle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(
                        pipelineName: secondPipelineName,
                        pipelineDefinition: CreatePipelineDefinition(secondPipelineName),
                        input: new
                        {
                            source = "queue-control-hot-add-paused-second"
                        },
                        source: "queue-control-hot-add-paused-second"))
                    .ConfigureAwait(false);

                Assert.NotNull(firstHandle);
                Assert.NotNull(secondHandle);

                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, firstHandle.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, secondHandle.Status);

                Assert.True(string.IsNullOrWhiteSpace(firstHandle.ExecutionId));
                Assert.True(string.IsNullOrWhiteSpace(secondHandle.ExecutionId));

                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, firstHandle.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Queued, secondHandle.Status);

                Assert.True(string.IsNullOrWhiteSpace(firstHandle.ExecutionId));
                Assert.True(string.IsNullOrWhiteSpace(secondHandle.ExecutionId));

                await controller.ResumeQueueAsync(
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                var firstFinal = await firstHandle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                var secondFinal = await secondHandle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(30));

                Assert.NotNull(firstFinal);
                Assert.NotNull(secondFinal);

                Assert.Equal(AiExecutionStatus.Completed, firstFinal.Status);
                Assert.Equal(AiExecutionStatus.Completed, secondFinal.Status);

                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, firstHandle.Status);
                Assert.Equal(AiRuntimeWorkerRunStatus.Completed, secondHandle.Status);

                Assert.False(string.IsNullOrWhiteSpace(firstHandle.ExecutionId));
                Assert.False(string.IsNullOrWhiteSpace(secondHandle.ExecutionId));
                Assert.NotEqual(firstHandle.ExecutionId, secondHandle.ExecutionId);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(firstHandle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        firstHandle.ExecutionId);
                }

                if (!string.IsNullOrWhiteSpace(secondHandle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        secondHandle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Creates a fully configured test host for queue-control tests.
        /// </summary>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateHostAsync()
        {
            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiRuntimePipelineBackgroundControllerQueueControlTests).Assembly);
                });
        }

        /// <summary>
        /// Creates runtime options for queue-control tests.
        /// </summary>
        /// <returns>The configured AI engine options.</returns>
        private static AiEngineOptions CreateOptions()
        {
            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Runtime",
                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = 1,
                    IdleDelay = TimeSpan.FromMilliseconds(1),
                    MaxCycles = 1000,
                    IgnoreConcurrencyConflicts = true
                },
                PipelineBackgroundController = new AiRuntimePipelineBackgroundControllerOptions
                {
                    MaxConcurrentRuns = 1,
                    QueueCapacity = 8,
                    RejectEnqueueWhenStopped = false,
                    StopOnFirstFailure = false,
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = 1,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(30)
                    }
                }
            };

            options.Snapshots.Enabled = false;

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        /// <summary>
        /// Creates a queue-control pipeline definition.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="signalKey">The optional signal key for the delay step.</param>
        /// <param name="delayMs">The delay in milliseconds.</param>
        /// <returns>The pipeline definition.</returns>
        private static AiPipelineDefinition CreatePipelineDefinition(
            string pipelineName,
            string? signalKey = null,
            int delayMs = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            var stepKey = string.IsNullOrWhiteSpace(signalKey)
                ? "hello-world"
                : "queue-control.delay";

            var config = new Dictionary<string, object?>
            {
                ["delayMs"] = delayMs
            };

            if (!string.IsNullOrWhiteSpace(signalKey))
            {
                config["signalKey"] = signalKey;
            }

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new[]
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "queue-control-step",
                        StepKey = stepKey,
                        Order = 1,
                        Config = config
                    }
                }
            };
        }

        /// <summary>
        /// Deletes the live DAG execution bundle for the specified execution.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="executionId">The execution identifier.</param>
        private static async Task CleanupExecutionAsync(
            IServiceProvider serviceProvider,
            string executionId)
        {
            ArgumentNullException.ThrowIfNull(serviceProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            var dagStore = serviceProvider.GetRequiredService<IAiDagExecutionStore>();

            await dagStore.DeleteExecutionBundleAsync(
                executionId);
        }
    }

    /// <summary>
    /// Signal registry used by queue-control delay test steps.
    /// </summary>
    internal static class QueueControlDelayStepSignalRegistry
    {
        private static readonly Dictionary<string, TaskCompletionSource<bool>> StartedSignals =
            new(StringComparer.Ordinal);

        private static readonly object Sync = new();

        /// <summary>
        /// Creates a signal entry for the specified key.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        public static void Create(
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            lock (Sync)
            {
                StartedSignals[signalKey] = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// Marks the controlled step as started.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        public static void MarkStarted(
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            lock (Sync)
            {
                if (StartedSignals.TryGetValue(signalKey, out var signal))
                {
                    signal.TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// Waits until the controlled step has started.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        /// <param name="timeout">The timeout.</param>
        public static async Task WaitForStartedAsync(
            string signalKey,
            TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            TaskCompletionSource<bool> signal;

            lock (Sync)
            {
                if (!StartedSignals.TryGetValue(signalKey, out signal!))
                {
                    throw new InvalidOperationException(
                        $"No queue-control delay signal was registered for key '{signalKey}'.");
                }
            }

            await signal.Task.WaitAsync(timeout);
        }

        /// <summary>
        /// Removes the signal entry for the specified key.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        public static void Remove(
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            lock (Sync)
            {
                StartedSignals.Remove(signalKey);
            }
        }

        private static AiRuntimeWorkerRunHandle CreateTestHandle()
{
    var completionSource = new TaskCompletionSource<AiExecutionRecord>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    return new AiRuntimeWorkerRunHandle(
        Guid.NewGuid().ToString("N"),
        completionSource.Task);
}
    }

    /// <summary>
    /// Controlled delay step used by queue-control integration tests.
    /// </summary>
    [AiStep("queue-control.delay")]
    public sealed class QueueControlDelayStep : IAiStep
    {
        /// <inheritdoc />
        public string Name => "queue-control.delay";

        /// <inheritdoc />
        public async Task<AiStepResult> ExecuteAsync(
            AiStepExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var helper = context.GetHelper();

            var signalKey = await helper.GetConfigAsync<string>(
                "signalKey",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(signalKey))
            {
                throw new InvalidOperationException(
                    "Missing required config value 'signalKey'.");
            }

            var delayMs = await helper.GetConfigAsync<int?>(
                "delayMs",
                cancellationToken).ConfigureAwait(false) ?? 300;

            QueueControlDelayStepSignalRegistry.MarkStarted(
                signalKey);

            await Task.Delay(
                delayMs,
                cancellationToken).ConfigureAwait(false);

            return AiStepResult.Ok(
                output: "Queue-control delay step completed.",
                data: helper.ToDictionary(new
                {
                    signalKey,
                    delayMs,
                    context.Record.ExecutionId
                }));
        }
    }
}