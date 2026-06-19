using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.Sample.External.Plugins.Steps.Steps;
using Xunit;
using Xunit.Abstractions;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Demo
{
    /// <summary>
    /// Integration tests for the enterprise runtime demo pipeline.
    /// </summary>
    [Collection("redis")]
    public sealed class AiEnterpriseRuntimeDemoPipelineTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiEnterpriseRuntimeDemoPipelineTests"/> class.
        /// </summary>
        /// <param name="output">The xUnit output helper.</param>
        public AiEnterpriseRuntimeDemoPipelineTests(
            ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>
        /// Verifies that the enterprise demo pipeline can be executed through the
        /// background controller with distributed runtime workers.
        /// </summary>
        [RedisFact]
        public async Task EnterpriseDemoPipeline_Should_Run_Through_Background_Controller()
        {
            const string pipelineName = "enterprise-runtime-demo";

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateEnterpriseDemoOptions(),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(DemoPassStep).Assembly);
                });

            var controller = host.ServiceProvider
                .GetRequiredService<IAiRuntimePipelineBackgroundController>();

            var dagStore = host.ServiceProvider
                .GetRequiredService<IAiDagExecutionStore>();

            var metrics = host.ServiceProvider
                .GetRequiredService<IAiRuntimeMetrics>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.AttachSnapshot(
                        new AiRuntimePipelineRunRequest
                        {
                            PipelineName = pipelineName,
                            PipelineJsonFilePath = "config/enterprise-demo-pipeline.json",
                            Input = new
                            {
                                source = "enterprise-demo-test",
                                demo = true
                            }
                        },
                        source: "enterprise-demo-test"));

                Assert.NotNull(handle);
                Assert.False(string.IsNullOrWhiteSpace(handle.RunId));

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(60));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));
                Assert.NotEqual(handle.RunId, handle.ExecutionId);
                Assert.Equal(handle.ExecutionId, final.ExecutionId);

                Assert.Contains("start", final.CompletedSteps);
                Assert.Contains("load-context", final.CompletedSteps);
                Assert.Contains("retrieve-policy", final.CompletedSteps);
                Assert.Contains("retrieve-customer-data", final.CompletedSteps);
                Assert.Contains("retrieve-risk-data", final.CompletedSteps);
                Assert.Contains("transient-provider-call", final.CompletedSteps);
                Assert.Contains("generate-large-context", final.CompletedSteps);
                Assert.Contains("compose-result", final.CompletedSteps);
                Assert.Contains("audit-result", final.CompletedSteps);
                Assert.Contains("persist-summary", final.CompletedSteps);
                Assert.Contains("final-validation", final.CompletedSteps);
                Assert.Contains("finalize", final.CompletedSteps);

                Assert.Equal(12, final.CompletedSteps.Count);

                var persistedRecord = await dagStore.GetRecordAsync(
                    handle.ExecutionId);

                Assert.NotNull(persistedRecord);
                Assert.Equal(AiExecutionStatus.Completed, persistedRecord!.Status);
                Assert.Equal(handle.ExecutionId, persistedRecord.ExecutionId);
                Assert.True(persistedRecord.IsTerminal);
                Assert.Equal(12, persistedRecord.CompletedSteps.Count);

                var persistedState = await dagStore.GetStateAsync(
                    handle.ExecutionId);

                Assert.NotNull(persistedState);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                Assert.NotEmpty(workerCycles);
                Assert.True(
                    workerCycles.Count >= 2,
                    $"Expected at least two distributed runtime workers to participate, but only '{workerCycles.Count}' reported activity.");

                _output.WriteLine(
                    $"Enterprise demo completed. RunId='{handle.RunId}', ExecutionId='{handle.ExecutionId}', CompletedSteps='{final.CompletedSteps.Count}', Status='{final.Status}'.");

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
                    await dagStore.DeleteExecutionBundleAsync(
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Verifies that the enterprise demo pipeline exercises retry behavior through
        /// the external flaky demo step.
        /// </summary>
        [RedisFact]
        public async Task EnterpriseDemoPipeline_Should_Retry_Flaky_Demo_Step()
        {
            const string pipelineName = "enterprise-runtime-demo";
            const string retriedStepName = "transient-provider-call";

            await using var host = await AiDagExecutionEngineFixture.CreateAsync(
                CreateEnterpriseDemoOptions(),
                configureServices: services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(DemoPassStep).Assembly);
                });

            var controller = host.ServiceProvider
                .GetRequiredService<IAiRuntimePipelineBackgroundController>();

            var dagStore = host.ServiceProvider
                .GetRequiredService<IAiDagExecutionStore>();

            var resolver = host.ServiceProvider
                .GetRequiredService<IAiExecutionStepResolver>();

            await controller.StartAsync();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                handle = await controller.EnqueueAsync(
                    AiRuntimeExecutionContextSnapshotTestFixture.AttachSnapshot(
                        new AiRuntimePipelineRunRequest
                        {
                            PipelineName = pipelineName,
                            PipelineJsonFilePath = "config/enterprise-demo-pipeline.json",
                            Input = new
                            {
                                source = "enterprise-demo-retry-test",
                                demo = true
                            }
                        },
                        source: "enterprise-demo-retry-test"));

                Assert.NotNull(handle);

                var final = await handle.Completion.WaitAsync(
                    TimeSpan.FromSeconds(60));

                Assert.NotNull(final);
                Assert.True(final.IsTerminal);
                Assert.Equal(AiExecutionStatus.Completed, final.Status);

                Assert.False(string.IsNullOrWhiteSpace(handle.ExecutionId));

                var state = await dagStore.GetStateAsync(
                    handle.ExecutionId);

                Assert.NotNull(state);

                var retriedStep = await resolver.GetStepAsync(
                    handle.ExecutionId,
                    retriedStepName,
                    state!,
                    CancellationToken.None);

                Assert.NotNull(retriedStep);
                Assert.Equal(AiStepExecutionStatus.Completed, retriedStep!.Status);
                Assert.NotNull(retriedStep.RetryState);
                Assert.True(
                    retriedStep.RetryState!.RetryCount >= 1,
                    $"Expected step '{retriedStepName}' to be retried at least once.");

                _output.WriteLine(
                    $"Flaky demo step recovered. ExecutionId='{handle.ExecutionId}', Step='{retriedStepName}', RetryCount='{retriedStep.RetryState!.RetryCount}'.");
            }
            finally
            {
                await controller.StopAsync();

                if (!string.IsNullOrWhiteSpace(handle?.ExecutionId))
                {
                    await dagStore.DeleteExecutionBundleAsync(
                        handle.ExecutionId);
                }
            }
        }

        /// <summary>
        /// Creates runtime options for the enterprise demo pipeline JSON.
        /// </summary>
        private static AiEngineOptions CreateEnterpriseDemoOptions()
        {
            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = "config/enterprise-demo-pipeline.json",

                RuntimeInstanceWorker = new AiRuntimeInstanceWorkerOptions
                {
                    MaxStepsPerCycle = 2,
                    IdleDelay = TimeSpan.FromMilliseconds(5),
                    MaxCycles = 5000,
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
                        WorkerCount = 3,
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
    }
}