using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Rag.Normalization;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Normalization;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Stores.Cache;
using Multiplexed.AI.Stores.Cache.Redis;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Helpers;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;
using StackExchange.Redis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using static Multiplexed.AI.Tests.Integration.Helpers.MetricsFactory;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// End-to-end Redis integration tests for distributed retry behavior in <see cref="AiDagExecutionEngine"/>.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Validate distributed retry behavior through the DAG engine.
    /// - Validate retry delay, retry promotion, and retry exhaustion.
    /// - Validate timeout recovery without mutating business retry count.
    ///
    /// RETRY MIGRATION:
    /// - These tests use step-level <c>config.retry</c>.
    /// - Legacy <c>Execution.RetryDelayMs</c> and <c>Execution.MaxRetries</c> are no longer used.
    ///
    /// RETENTION MIGRATION:
    /// - These tests do not wire legacy retention services.
    /// - Engine creation goes through the production-like migrated fixture.
    /// - Pipelines without retention config run retention as no-op.
    /// </remarks>
    [Collection("redis")]
    public sealed class AiDagExecutionEngineRedisRetryIntegrationTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineRedisRetryIntegrationTests"/> class.
        /// </summary>
        /// <param name="fixture">The Redis fixture.</param>
        public AiDagExecutionEngineRedisRetryIntegrationTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            _connection = fixture.Connection;
        }

        /// <summary>
        /// Verifies that the first step failure moves the step to WaitingForRetry.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Move_Step_To_WaitingForRetry_On_FirstFailure()
        {
            await using var host = await CreateRetryHostAsync(
                pipelineName: "dag-retry-fail-once",
                stepKey: "fail-once-then-succeed",
                maxRetries: 2,
                baseDelayMs: 1000,
                maxDelayMs: 1000);

            var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

            var created = await host.Engine.CreateAsync("dag-retry-fail-once", "Marco");

            try
            {
                await ExecuteIgnoringFailureAsync(host.Engine, created.ExecutionId);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();
                var state = await dagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(state);

                var step = stateWriter.GetOrCreateStep(state!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
                Assert.Equal(1, step.RetryState?.RetryCount);
                Assert.NotNull(step.RetryState?.NextRetryAtUtc);
                Assert.Null(step.ClaimedBy);
                Assert.Null(step.ClaimToken);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that a retryable step is not re-executed before the retry delay opens.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Not_Reexecute_Step_Before_RetryDelay()
        {
            await using var host = await CreateRetryHostAsync(
                pipelineName: "dag-retry-delay",
                stepKey: "fail-once-then-succeed",
                maxRetries: 1,
                baseDelayMs: 5000,
                maxDelayMs: 5000);

            var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

            var created = await host.Engine.CreateAsync("dag-retry-delay", "Marco");

            try
            {
                await ExecuteIgnoringFailureAsync(host.Engine, created.ExecutionId);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                var stateAfterFirstFailure =
                    await dagStore.GetStateAsync(created.ExecutionId);

                Assert.NotNull(
                    stateAfterFirstFailure);

                var stepAfterFirstFailure =
                    stateWriter.GetOrCreateStep(
                        stateAfterFirstFailure!,
                        "retry-step");

                Assert.Equal(
                    AiStepExecutionStatus.WaitingForRetry,
                    stepAfterFirstFailure.Status);

                Assert.Equal(
                    1,
                    stepAfterFirstFailure.RetryState?.RetryCount);

                Assert.NotNull(
                    stepAfterFirstFailure.RetryState?.NextRetryAtUtc);

                var nextRetryAtUtc =
                    stepAfterFirstFailure.RetryState!.NextRetryAtUtc!.Value;

                Assert.True(
                    nextRetryAtUtc > DateTime.UtcNow,
                    $"Expected retry window to still be in the future after first failure. NextRetryAtUtc='{nextRetryAtUtc:O}', Now='{DateTime.UtcNow:O}'.");

                var second =
                    await host.Engine.ExecuteNextAsync(
                        created.ExecutionId);

                var stateAfterSecondExecute =
                    await dagStore.GetStateAsync(
                        created.ExecutionId);

                Assert.NotNull(
                    stateAfterSecondExecute);

                var stepAfterSecondExecute =
                    stateWriter.GetOrCreateStep(
                        stateAfterSecondExecute!,
                        "retry-step");

                Assert.Equal(
                    AiStepExecutionStatus.WaitingForRetry,
                    stepAfterSecondExecute.Status);

                Assert.Equal(
                    1,
                    stepAfterSecondExecute.RetryState?.RetryCount);

                Assert.NotNull(
                    stepAfterSecondExecute.RetryState?.NextRetryAtUtc);

                Assert.Equal(
                    nextRetryAtUtc,
                    stepAfterSecondExecute.RetryState!.NextRetryAtUtc!.Value);

                Assert.True(
                    stepAfterSecondExecute.RetryState.NextRetryAtUtc > DateTime.UtcNow,
                    $"Expected retry window to remain closed after second ExecuteNextAsync. " +
                    $"SecondStatus='{second.Status}', NextRetryAtUtc='{stepAfterSecondExecute.RetryState.NextRetryAtUtc:O}', Now='{DateTime.UtcNow:O}'.");
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that a step retries and completes when the retry window is open.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Retry_And_Complete_When_RetryWindow_Is_Open()
        {
            await using var host = await CreateRetryHostAsync(
                pipelineName: "dag-retry-fail-once",
                stepKey: "fail-once-then-succeed",
                maxRetries: 2,
                baseDelayMs: 100,
                maxDelayMs: 100);

            var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

            var created = await host.Engine.CreateAsync("dag-retry-fail-once", "Marco");

            try
            {
                await ExecuteIgnoringFailureAsync(host.Engine, created.ExecutionId);

                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                var beforeRetry = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(beforeRetry);

                var retryStep = stateWriter.GetOrCreateStep(beforeRetry!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, retryStep.Status);
                Assert.NotNull(retryStep.RetryState?.NextRetryAtUtc);

                await WaitForRetryWindowAsync(
                    dagStore,
                    stateWriter,
                    created.ExecutionId,
                    "retry-step");

                await ExecuteIgnoringFailureAsync(host.Engine, created.ExecutionId);

                var finalState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(finalState);

                var finalStep = stateWriter.GetOrCreateStep(finalState!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.Completed, finalStep.Status);
                Assert.Equal(1, finalStep.RetryState?.RetryCount);
                Assert.Null(finalStep.RetryState?.NextRetryAtUtc);

                var finalRecord = await dagStore.GetRecordAsync(created.ExecutionId);
                Assert.NotNull(finalRecord);
                Assert.Equal(AiExecutionStatus.Completed, finalRecord!.Status);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that retry exhaustion converges to a failed execution.
        /// </summary>
        [RedisFact]
        public async Task ExecuteAllAsync_Should_Fail_After_MaxRetries_Are_Exhausted()
        {
            await using var host = await CreateRetryHostAsync(
                pipelineName: "dag-retry-always-fail",
                stepKey: "always-fail",
                maxRetries: 3,
                baseDelayMs: 100,
                maxDelayMs: 100);

            var stateWriter = host.ServiceProvider.GetRequiredService<IAiExecutionStateWriter>();

            var created = await host.Engine.CreateAsync("dag-retry-always-fail", "Marco");

            try
            {
                var dagStore = host.ServiceProvider.GetRequiredService<IAiDagExecutionStore>();

                for (var i = 0; i < 10; i++)
                {
                    await ExecuteIgnoringFailureAsync(host.Engine, created.ExecutionId);

                    var state = await dagStore.GetStateAsync(created.ExecutionId);
                    Assert.NotNull(state);

                    var step = stateWriter.GetOrCreateStep(state!, "retry-step");

                    if (step.Status == AiStepExecutionStatus.WaitingForRetry)
                    {
                        await WaitForRetryWindowAsync(
                            dagStore,
                            stateWriter,
                            created.ExecutionId,
                            "retry-step");
                    }

                    if (step.Status == AiStepExecutionStatus.Failed)
                    {
                        break;
                    }
                }

                var finalRecord = await dagStore.GetRecordAsync(created.ExecutionId);
                Assert.NotNull(finalRecord);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord!.Status);

                var finalState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(finalState);

                var finalStep = stateWriter.GetOrCreateStep(finalState!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.Failed, finalStep.Status);
                Assert.Equal(finalStep.Retry?.MaxRetries, finalStep.RetryState?.RetryCount);
                Assert.Null(finalStep.RetryState?.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
            }
        }

        /// <summary>
        /// Verifies that timeout recovery increments recovery count without incrementing retry count.
        /// </summary>
        [RedisFact]
        public async Task RecoverTimedOutStepsAsync_Should_Increment_RecoveryCount_Without_Incrementing_RetryCount()
        {
            var dagStore = CreateDagStore();
            var executionId = Guid.NewGuid().ToString("N");

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "dag-recovery",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running,
                CompletedSteps = new List<string>()
            };

            var state = new AiExecutionState
            {
                ExecutionId = executionId
            };

            state.Steps["retry-step"] = new AiStepState
            {
                StepName = "retry-step",
                Status = AiStepExecutionStatus.Running,
                ClaimedBy = "worker-old",
                ClaimToken = "claim-old",
                ClaimedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-9),
                ClaimTimeoutSeconds = 30,
                RetryState = new AiStepRetryState
                {
                    RetryCount = 1
                },
                RecoveryCount = 0
            };

            await dagStore.CreateAsync(record, state);

            try
            {
                var recovered = await dagStore.RecoverTimedOutStepsAsync(executionId);

                Assert.Equal(1, recovered);

                var snapshot = await dagStore.GetStateAsync(executionId);

                Assert.NotNull(snapshot);

                var step = snapshot!.Steps["retry-step"];

                Assert.Equal(AiStepExecutionStatus.Ready, step.Status);
                Assert.Null(step.ClaimedBy);
                Assert.Null(step.ClaimToken);
                Assert.Null(step.ClaimedAtUtc);
                Assert.Null(step.LeaseExpiresAtUtc);
                Assert.Equal(1, step.RetryState?.RetryCount);
                Assert.Equal(1, step.RecoveryCount);
            }
            finally
            {
                await CleanupDagExecutionAsync(executionId);
            }
        }

        /// <summary>
        /// Executes one step and ignores expected test failures.
        /// </summary>
        private static async Task ExecuteIgnoringFailureAsync(
            AiDagExecutionEngine engine,
            string executionId)
        {
            try
            {
                await engine.ExecuteNextAsync(executionId);
            }
            catch
            {
                // Expected in tests where the step throws intentionally.
            }
        }

        /// <summary>
        /// Waits until the retry window opens.
        /// </summary>
        private static async Task WaitForRetryWindowAsync(
            IAiDagExecutionStore dagStore,
            IAiExecutionStateWriter stateWriter,
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 50; i++)
            {
                var state = await dagStore.GetStateAsync(executionId, cancellationToken);

                if (state is null)
                {
                    throw new InvalidOperationException($"State '{executionId}' was not found.");
                }

                var step = stateWriter.GetOrCreateStep(state, stepName);

                if (step.Status == AiStepExecutionStatus.Failed ||
                    step.Status == AiStepExecutionStatus.Completed)
                {
                    return;
                }

                var nextRetryAtUtc = step.RetryState?.NextRetryAtUtc;

                if (!nextRetryAtUtc.HasValue)
                {
                    return;
                }

                var delay = nextRetryAtUtc.Value - DateTime.UtcNow;

                if (delay <= TimeSpan.Zero)
                {
                    return;
                }

                var wait = delay < TimeSpan.FromMilliseconds(25)
                    ? delay
                    : TimeSpan.FromMilliseconds(25);

                await Task.Delay(wait, cancellationToken);
            }

            throw new TimeoutException(
                $"Retry window did not open in time for step '{stepName}' in execution '{executionId}'.");
        }

        /// <summary>
        /// Creates a production-like host for a retry pipeline.
        /// </summary>
        private async Task<AiDagExecutionEngineTestHost> CreateRetryHostAsync(
            string pipelineName,
            string stepKey,
            int maxRetries,
            int baseDelayMs,
            int maxDelayMs)
        {
            var jsonFileName = $"{pipelineName}-{Guid.NewGuid():N}.json";
            var jsonPath = WriteRetryPipelineDefinitionToConfig(
                jsonFileName,
                pipelineName,
                stepKey,
                maxRetries,
                baseDelayMs,
                maxDelayMs);

            var options = new AiEngineOptions
            {
                DefaultPipelineDefinitionSource = "Json",
                JsonPipelineDefinitionFilePath = "config/" + jsonFileName,

                Snapshots = new AiExecutionSnapshotOptions
                {
                    Enabled = false
                },

                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = false,
                    AutoCleanupOnFailed = false,
                    SuppressCleanupExceptions = true
                }
            };

            var host = await AiDagExecutionEngineFixture.CreateAsync(
                options,
                services =>
                {
                    services.AddAiStepsFromAssemblies(
                        typeof(AiRuntimeAssemblyMarker).Assembly,
                        typeof(AiDagExecutionEngineRedisRetryIntegrationTests).Assembly);
                },
                mongoConnectionString: "mongodb://localhost:27017",
                mongoDatabaseName: "multiplexed_ai_tests",
                redisConnectionString: "localhost:6379");

            host.RegisterDisposableFile(jsonPath);

            return host;
        }

        /// <summary>
        /// Writes a retry pipeline definition using step-level config.retry.
        /// </summary>
        private static string WriteRetryPipelineDefinitionToConfig(
            string jsonFileName,
            string pipelineName,
            string stepKey,
            int maxRetries,
            int baseDelayMs,
            int maxDelayMs)
        {
            var root = new
            {
                pipelines = new[]
                {
                    new
                    {
                        name = pipelineName,
                        version = "1",
                        executionMode = "Dag",
                        steps = new[]
                        {
                            new
                            {
                                name = "retry-step",
                                stepKey,
                                order = 1,
                                dependsOn = Array.Empty<string>(),
                                config = new
                                {
                                    retry = new
                                    {
                                        policies = new[]
                                        {
                                            "retry.transient.default"
                                        },
                                        maxRetries,
                                        strategy = "Fixed",
                                        baseDelayMs,
                                        maxDelayMs,
                                        jitter = false
                                    }
                                }
                            }
                        }
                    }
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

            var filePath = Path.Combine(configDir, jsonFileName);

            File.WriteAllText(filePath, json);

            return filePath;
        }

        /// <summary>
        /// Creates a distributed DAG store for Redis/Lua recovery validation.
        /// </summary>
        private IAiDagExecutionStore CreateDagStore()
        {
            var logger = new NoopLogger();
            var keyBuilder = new AiExecutionKeyBuilder();
            var metrics = MetricsFactory.Create();

            var normalizers = new DefaultAiStepResultNormalizerPipeline(
                [new RagStepResultNormalizer()]);

            IRedisDagStoreServices services = new RedisDagStoreServices(
             _connection,
             keyBuilder,
             logger,
             metrics,
             normalizers);

            return new RedisAiDagExecutionStore(
                services);
        }

        /// <summary>
        /// Best-effort cleanup of Redis DAG execution keys.
        /// </summary>
        private async Task CleanupDagExecutionAsync(
            string executionId)
        {
            var db = _connection.GetDatabase();

            var recordKey = $"ai:execution:record:{executionId}";
            var stateKey = $"ai:execution:state:{executionId}";
            var stepsIndexKey = $"ai:execution:steps:{executionId}";

            var stepNames = await db.SetMembersAsync(stepsIndexKey);

            foreach (var stepName in stepNames)
            {
                if (stepName.IsNullOrEmpty)
                {
                    continue;
                }

                await db.KeyDeleteAsync($"ai:execution:step:{executionId}:{stepName}");
            }

            await db.KeyDeleteAsync(recordKey);
            await db.KeyDeleteAsync(stateKey);
            await db.KeyDeleteAsync(stepsIndexKey);
        }
    }

    /// <summary>
    /// Test-host extension methods.
    /// </summary>
    internal static class AiDagExecutionEngineTestHostFileCleanupExtensions
    {
        private static readonly ConditionalWeakTable<AiDagExecutionEngineTestHost, List<string>> Files = new();

        /// <summary>
        /// Registers a temporary file for cleanup when the test completes.
        /// </summary>
        public static void RegisterDisposableFile(
            this AiDagExecutionEngineTestHost host,
            string filePath)
        {
            var files = Files.GetOrCreateValue(host);
            files.Add(filePath);
        }

        /// <summary>
        /// Deletes temporary files registered for a host.
        /// </summary>
        public static void CleanupRegisteredFiles(
            this AiDagExecutionEngineTestHost host)
        {
            if (!Files.TryGetValue(host, out var files))
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}
