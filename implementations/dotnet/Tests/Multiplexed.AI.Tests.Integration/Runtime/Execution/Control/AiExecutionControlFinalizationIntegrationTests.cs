using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
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
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using System.Collections.Concurrent;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Control
{
    /// <summary>
    /// Integration tests proving that execution control cancellation overrides terminal DAG completion.
    /// </summary>
    /// <remarks>
    /// These tests validate the finalization path where already-claimed work may complete
    /// successfully after a cancellation request. In that case, DAG convergence may naturally
    /// produce a completed status, but durable execution control state must take precedence
    /// and persist the execution as cancelled.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiExecutionControlFinalizationIntegrationTests
    {
        /// <summary>
        /// Verifies that a cancellation request made while a claimed step is running causes
        /// terminal finalization to persist the execution as cancelled instead of completed.
        /// </summary>
        [RedisFact]
        public async Task RunningExecution_WhenCancelledBeforeTerminalFinalization_ShouldPersistCancelledStatus()
        {
            var signalKey = $"execution-control-finalization-{Guid.NewGuid():N}";
            var pipelineName = $"execution-control-finalization-{Guid.NewGuid():N}";

            ExecutionControlFinalizationSignalRegistry.Create(
                signalKey);

            await using var host = await CreateHostAsync(
                pipelineName,
                signalKey);

            var controller = host.ServiceProvider.GetRequiredService<IAiRuntimePipelineBackgroundController>();
            var controlService = host.ServiceProvider.GetRequiredService<IAiExecutionControlService>();
            var controlStore = host.ServiceProvider.GetRequiredService<IAiExecutionControlStore>();
            var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;
            string? executionId = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = pipelineName,
                        ExecutionContextSnapshot = CreateExecutionContextSnapshot(
                            pipelineName),
                        PipelineDefinition = CreatePipelineDefinition(
                            pipelineName,
                            signalKey),
                        Input = new
                        {
                            source = "execution-control-finalization-test",
                            cancellationOverride = true
                        }
                    });

                Assert.NotNull(handle);

                executionId =
                    await WaitForStepStartOrFailAsync(
                            signalKey,
                            handle,
                            TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);

                Assert.False(
                    string.IsNullOrWhiteSpace(executionId));

                await controlService.CancelExecutionAsync(
                        executionId,
                        reason: "test cancellation while claimed work is running",
                        requestedBy: "integration-test")
                    .ConfigureAwait(false);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(60));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Cancelled, final.Status);
                Assert.Equal(executionId, final.ExecutionId);

                var persistedRecord = await dagStore.GetRecordAsync(
                    executionId);

                Assert.NotNull(persistedRecord);
                Assert.True(persistedRecord!.IsTerminal);
                Assert.Equal(AiExecutionStatus.Cancelled, persistedRecord.Status);

                Assert.Contains(
                    "controlled-finalization-step",
                    persistedRecord.CompletedSteps);

                var controlState = await controlStore.GetAsync(
                    executionId);

                Assert.NotNull(controlState);
                Assert.Equal(AiExecutionControlStatus.Cancelled, controlState!.Status);
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(executionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        executionId);
                }
                else if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await CleanupExecutionAsync(
                        host.ServiceProvider,
                        handle.ExecutionId);
                }

                ExecutionControlFinalizationSignalRegistry.Remove(
                    signalKey);
            }
        }

        /// <summary>
        /// Creates a fully configured test host for execution-control finalization tests.
        /// </summary>
        /// <param name="pipelineName">The runtime pipeline name.</param>
        /// <param name="signalKey">The signal key used by the controlled test step.</param>
        /// <returns>The created test host.</returns>
        private static async Task<AiDagExecutionEngineTestHost> CreateHostAsync(
            string pipelineName,
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            return await AiDagExecutionEngineFixture.CreateAsync(
                CreateOptions(),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiExecutionControlFinalizationIntegrationTests).Assembly);
                });
        }

        /// <summary>
        /// Creates runtime options for the execution-control finalization scenario.
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

            options.Observability.EnableTracing = true;
            options.Observability.EnableInMemoryRecording = true;

            options.Snapshots.Enabled = false;

            options.Cleanup.AutoCleanupOnCompleted = false;
            options.Cleanup.AutoCleanupOnFailed = false;
            options.Cleanup.SuppressCleanupExceptions = true;

            return options;
        }

        /// <summary>
        /// Creates the controlled pipeline definition used to test cancellation finalization.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <param name="signalKey">The step signal key.</param>
        /// <returns>The pipeline definition.</returns>
        private static AiPipelineDefinition CreatePipelineDefinition(
            string pipelineName,
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new[]
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "controlled-finalization-step",
                        StepKey = "execution-control.finalization.delay",
                        Order = 1,
                        Config = new Dictionary<string, object?>
                        {
                            ["signalKey"] = signalKey,
                            ["delayMs"] = 250
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a minimal execution context snapshot for direct runtime integration tests.
        /// </summary>
        /// <param name="pipelineName">The pipeline name.</param>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            return new ExecutionContextSnapshot
            {
                ContextKey = $"ctx-{pipelineName}",
                Project = "runtime-integration-tests",
                UserId = "execution-control-finalization-test",
                TenantId = "test-tenant",
                TenantGroupId = "test-tenant",
                CurrentNamespace = "runtime-integration-tests",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "runtime-integration-tests",
                        Trns = new HashSet<string>
                        {
                            "trn:runtime-integration-tests:execution:run",
                            "trn:runtime-integration-tests:execution:cancel",
                            "trn:runtime-integration-tests:execution:read"
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Waits until the controlled step starts, or fails with the runtime completion result
        /// when the run terminates before the controlled step can signal startup.
        /// </summary>
        /// <param name="signalKey">The step signal key.</param>
        /// <param name="handle">The runtime worker run handle.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The durable execution identifier captured when the step starts.</returns>
        private static async Task<string> WaitForStepStartOrFailAsync(
            string signalKey,
            AiRuntimeWorkerRunHandle handle,
            TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);
            ArgumentNullException.ThrowIfNull(handle);

            var startedTask =
                ExecutionControlFinalizationSignalRegistry.WaitForStartedAsync(
                    signalKey,
                    timeout);

            var completionTask =
                handle.Completion;

            var completedTask =
                await Task.WhenAny(
                        startedTask,
                        completionTask,
                        Task.Delay(timeout))
                    .ConfigureAwait(false);

            if (completedTask == startedTask)
            {
                return await startedTask
                    .ConfigureAwait(false);
            }

            if (completedTask == completionTask)
            {
                var result =
                    await completionTask
                        .ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"The runtime run completed before the controlled finalization step started. " +
                    $"Status='{result.Status}', IsTerminal='{result.IsTerminal}', ExecutionId='{result.ExecutionId}'");
            }

            throw new TimeoutException(
                $"The controlled finalization step did not start within '{timeout}'. " +
                $"HandleExecutionId='{handle.ExecutionId}'.");
        }

        /// <summary>
        /// Deletes the live DAG execution bundle and its durable execution control state.
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
            var controlStore = serviceProvider.GetRequiredService<IAiExecutionControlStore>();

            await dagStore.DeleteExecutionBundleAsync(
                executionId);

            await controlStore.DeleteAsync(
                executionId);
        }
    }

    /// <summary>
    /// Signal registry used by the execution-control finalization integration test step.
    /// </summary>
    internal static class ExecutionControlFinalizationSignalRegistry
    {
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> StartedSignals =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Creates a signal entry for the specified key.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        public static void Create(
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            StartedSignals[signalKey] = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Marks the controlled step as started.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        public static void MarkStarted(
            string signalKey,
            string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            if (StartedSignals.TryGetValue(signalKey, out var signal))
            {
                signal.TrySetResult(executionId);
            }
        }

        /// <summary>
        /// Waits until the controlled step has started.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        /// <param name="timeout">The wait timeout.</param>
        /// <returns>The durable execution identifier.</returns>
        public static async Task<string> WaitForStartedAsync(
            string signalKey,
            TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            if (!StartedSignals.TryGetValue(signalKey, out var signal))
            {
                throw new InvalidOperationException(
                    $"No execution-control finalization signal was registered for key '{signalKey}'.");
            }

            return await signal.Task
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the signal entry for the specified key.
        /// </summary>
        /// <param name="signalKey">The signal key.</param>
        public static void Remove(
            string signalKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalKey);

            StartedSignals.TryRemove(
                signalKey,
                out _);
        }
    }

    /// <summary>
    /// Controlled test step that signals when it starts and then completes successfully after a delay.
    /// </summary>
    [AiStep("execution-control.finalization.delay")]
    public sealed class ExecutionControlFinalizationDelayStep : IAiStep
    {
        /// <inheritdoc />
        public string Name => "execution-control.finalization.delay";

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
                cancellationToken).ConfigureAwait(false) ?? 250;

            ExecutionControlFinalizationSignalRegistry.MarkStarted(
                signalKey,
                context.Record.ExecutionId);

            await Task.Delay(
                delayMs,
                cancellationToken).ConfigureAwait(false);

            return AiStepResult.Ok(
                output: "Controlled finalization delay step completed.",
                data: helper.ToDictionary(new
                {
                    signalKey,
                    delayMs,
                    context.Record.ExecutionId
                }));
        }
    }
}