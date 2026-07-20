using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.Execution.Convergence;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.State;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Runtime.Pipeline.Steps.Test;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;
using Multiplexed.Realtime.DI;
using StackExchange.Redis;
using Xunit;
using static Multiplexed.AI.Tests.Integration.Runtime.Execution.Fixtures.AiDagExecutionEngineTestHost;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// Distributed retry budget tests for DAG execution.
    ///
    /// PURPOSE:
    /// Locks the official retry semantics around RetryCount and MaxRetries.
    ///
    /// OFFICIAL SEMANTICS:
    /// - MaxRetries = number of retries allowed after the initial attempt
    /// - RetryCount = number of retries already consumed
    /// - initial execution does not increment RetryCount
    /// - retry eligibility is: RetryCount &lt; MaxRetries
    ///
    /// THIS TEST SUITE PROVES:
    /// - MaxRetries = 0 => no retry, immediate terminal failure
    /// - MaxRetries = 1 => one distributed retry only
    /// - MaxRetries = 2 => two distributed retries only
    /// - RetryCount is incremented only when a retry is officially scheduled
    /// - terminal failure does not leave a pending retry window behind
    /// </summary>
    [Collection("redis")]
    public sealed class AiDagExecutionEngineRedisRetryBudgetTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineRedisRetryBudgetTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared Redis fixture.</param>
        public AiDagExecutionEngineRedisRetryBudgetTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            _connection = fixture.Connection;
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Fail_Immediately_When_MaxRetries_Is_Zero()
        {
            var provider = CreateTestServiceProvider("retry-budget-maxretries-0.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "retry-budget-maxretries-0",
                input: "test");

            try
            {
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected:
                    // the test step fails by design.
                }
                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
                var finalRecord = await WaitForTerminalFailureAsync(
                    engine,
                    dagStore,
                    stateWriter,
                    record.ExecutionId);

                var finalState = await dagStore.GetStateAsync(record.ExecutionId);

                Assert.NotNull(finalState);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord.Status);

                var step = finalState!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.Failed, step.Status);
                Assert.Equal(0, step.RetryState?.RetryCount);
                Assert.Equal(0, step.Retry?.MaxRetries);
                Assert.Null(step.RetryState?.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Allow_Exactly_One_Retry_When_MaxRetries_Is_One()
        {
            var provider = CreateTestServiceProvider("retry-budget-maxretries-1.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "retry-budget-maxretries-1",
                input: "test");

            try
            {
                // Initial failure -> should schedule retry #1
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected.
                }

                var stateAfterFirstFailure = await dagStore.GetStateAsync(record.ExecutionId);
                var recordAfterFirstFailure = await dagStore.GetRecordAsync(record.ExecutionId);

                Assert.NotNull(stateAfterFirstFailure);
                Assert.NotNull(recordAfterFirstFailure);

                var stepAfterFirstFailure = stateAfterFirstFailure!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, stepAfterFirstFailure.Status);
                Assert.Equal(1, stepAfterFirstFailure.RetryState?.RetryCount);
                Assert.Equal(1, stepAfterFirstFailure.Retry?.MaxRetries);
                Assert.True(stepAfterFirstFailure.RetryState?.NextRetryAtUtc.HasValue);

                Assert.Equal(AiExecutionStatus.Waiting, recordAfterFirstFailure!.Status);
                Assert.False(recordAfterFirstFailure.IsTerminal);
                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                // Second failure -> retry budget exhausted -> terminal failure
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected.
                }


                var finalRecord = await WaitForTerminalFailureAsync(
                    engine,
                    dagStore,
                    stateWriter,
                    record.ExecutionId);

                var finalState = await dagStore.GetStateAsync(record.ExecutionId);

                Assert.NotNull(finalState);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord.Status);

                var finalStep = finalState!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.Failed, finalStep.Status);
                Assert.Equal(1, finalStep.RetryState?.RetryCount);
                Assert.Equal(1, finalStep.Retry?.MaxRetries);
                Assert.Null(finalStep.RetryState?.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Allow_Exactly_Two_Retries_When_MaxRetries_Is_Two()
        {
            var provider = CreateTestServiceProvider("retry-budget-maxretries-2.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "retry-budget-maxretries-2",
                input: "test");

            try
            {
                // Failure #1 -> schedules retry #1
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected.
                }

                var stateAfterFirstFailure = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(stateAfterFirstFailure);

                var stepAfterFirstFailure = stateAfterFirstFailure!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, stepAfterFirstFailure.Status);
                Assert.Equal(1, stepAfterFirstFailure.RetryState?.RetryCount);
                Assert.Equal(2, stepAfterFirstFailure.Retry?.MaxRetries);
                Assert.True(stepAfterFirstFailure.RetryState?.NextRetryAtUtc.HasValue);
                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                // Failure #2 -> schedules retry #2
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected.
                }

                var stateAfterSecondFailure = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(stateAfterSecondFailure);

                var stepAfterSecondFailure = stateAfterSecondFailure!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, stepAfterSecondFailure.Status);
                Assert.Equal(2, stepAfterSecondFailure.RetryState?.RetryCount);
                Assert.Equal(2, stepAfterSecondFailure.Retry?.MaxRetries);
                Assert.True(stepAfterSecondFailure.RetryState?.NextRetryAtUtc.HasValue);
            
                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                // Failure #3 -> no more retry budget -> terminal failure
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected.
                }

                var finalRecord = await WaitForTerminalFailureAsync(
                    engine,
                    dagStore,
                    stateWriter,
                    record.ExecutionId);

                var finalState = await dagStore.GetStateAsync(record.ExecutionId);

                Assert.NotNull(finalState);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord.Status);

                var finalStep = finalState!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.Failed, finalStep.Status);
                Assert.Equal(2, finalStep.RetryState?.RetryCount);
                Assert.Equal(2, finalStep.Retry?.MaxRetries);
                Assert.Null(finalStep.RetryState?.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [Fact]
        public void SelectReadySteps_Should_Return_Step_When_Retry_Window_Reached()
        {
            var pipeline = CreateSingleStepPipeline();
            var state = new AiExecutionState();
            var writer = new DefaultAiExecutionStateWriter();

            var step = writer.GetOrCreateStep(state, "start");
            step.Status = AiStepExecutionStatus.WaitingForRetry;
            step.RetryState = new AiStepRetryState
            {
                NextRetryAtUtc = DateTime.UtcNow.AddMilliseconds(-1)
            };

            var result = AiPipelineDagStepSelector.SelectReadySteps(
                pipeline,
                state,
                writer,
                DateTime.UtcNow);

            Assert.Single(result);

            var updatedStep = writer.GetOrCreateStep(state, "start");
            Assert.True(updatedStep.IsSchedulable);
        }

        [Fact]
        public void SelectReadySteps_Should_Not_Return_Retry_Step_If_Dependencies_Not_Completed()
        {
            var pipeline = CreateTwoStepPipeline();
            var state = new AiExecutionState();
            var writer = new DefaultAiExecutionStateWriter();

            var step1 = writer.GetOrCreateStep(state, "start");
            step1.Status = AiStepExecutionStatus.WaitingForRetry;
            step1.RetryState = new AiStepRetryState
            {
                NextRetryAtUtc = DateTime.UtcNow.AddMilliseconds(-1)
            };

            var step2 = writer.GetOrCreateStep(state, "step2");
            step2.Status = AiStepExecutionStatus.None;

            var result = AiPipelineDagStepSelector.SelectReadySteps(
                pipeline,
                state,
                writer,
                DateTime.UtcNow);

            Assert.Contains(result, x => x.Name == "start");
            Assert.DoesNotContain(result, x => x.Name == "step2");
        }

        [Fact]
        public void Convergence_Should_Return_Running_When_Retry_Becomes_Executable()
        {
            var pipeline = CreateSingleStepPipeline();
            var state = new AiExecutionState();
            var writer = new DefaultAiExecutionStateWriter();

            var step = writer.GetOrCreateStep(state, "start");
            step.Status = AiStepExecutionStatus.WaitingForRetry;
            step.RetryState = new AiStepRetryState
            {
                NextRetryAtUtc = DateTime.UtcNow.AddMilliseconds(-1)
            };

            var result = AiDagExecutionConvergenceEvaluator.Evaluate(
                pipeline,
                state,
                writer,
                DateTime.UtcNow);

            Assert.Equal(AiExecutionStatus.Running, result.Status);
        }

        // ==============================
        // HELPERS
        // ==============================

        private static ResolvedAiPipeline CreateSingleStepPipeline()
        {
            return new ResolvedAiPipeline
            {
                Steps = new List<ResolvedAiPipelineStep>
        {
            new()
            {
                Name = "start",
                Order = 1,
                StepKey = "hello-world",
                DependsOn = Array.Empty<string>()
            }
        }
            };
        }

        private static ResolvedAiPipeline CreateTwoStepPipeline()
        {
            return new ResolvedAiPipeline
            {
                Steps = new List<ResolvedAiPipelineStep>
        {
            new()
            {
                Name = "start",
                Order = 1,
                StepKey = "hello-world",
                DependsOn = Array.Empty<string>()
            },
            new()
            {
                Name = "step2",
                Order = 2,
                StepKey = "hello-world",
                DependsOn = new[] { "start" }
            }
        }
            };
        }

        /// <summary>
        /// Waits until the retry window for a step becomes due.
        ///
        /// The helper polls persisted distributed state rather than relying on local timing.
        /// </summary>
        private static async Task WaitUntilRetryWindowOpensAsync(
            IAiDagExecutionStore dagStore,
            IAiExecutionStateWriter writer,
            string executionId,
            string stepName,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 100; i++)
            {
                var state = await dagStore.GetStateAsync(executionId, cancellationToken)
                    ?? throw new InvalidOperationException($"State '{executionId}' not found.");

                var step = writer.GetOrCreateStep(state, stepName);

                if (step.RetryState == null || !step.RetryState.NextRetryAtUtc.HasValue)
                    return;

                var delay = step.RetryState.NextRetryAtUtc.Value - DateTime.UtcNow;

                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay < TimeSpan.FromMilliseconds(50)
                    ? delay
                    : TimeSpan.FromMilliseconds(50), cancellationToken);
            }

            throw new TimeoutException("Retry window did not open.");
        }

        /// <summary>
        /// Drives the execution until terminal failure is projected by the distributed runtime.
        ///
        /// The step state remains the source of truth while the global record is the projected summary.
        /// </summary>
        private static async Task<AiExecutionRecord> WaitForTerminalFailureAsync(
            AiDagExecutionEngine engine,
            IAiDagExecutionStore dagStore,
            IAiExecutionStateWriter writer,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    var record = await engine.ExecuteNextAsync(executionId, cancellationToken);

                    if (record.IsTerminal)
                        return record;
                }
                catch
                {
                    // expected
                }

                var state = await dagStore.GetStateAsync(executionId, cancellationToken)
                    ?? throw new InvalidOperationException("State missing");

                var step = writer.GetOrCreateStep(state, "start");

                if (step.Status == AiStepExecutionStatus.WaitingForRetry)
                {
                    await WaitUntilRetryWindowOpensAsync(
                        dagStore,
                        writer,
                        executionId,
                        "start",
                        cancellationToken);
                }

                var snapshot = await dagStore.GetRecordAsync(executionId, cancellationToken);

                if (snapshot?.IsTerminal == true)
                    return snapshot;

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException("Did not converge.");
        }

        /// <summary>
        /// Creates a test service provider using the real runtime DI and the shared Redis connection.
        /// </summary>
        private ServiceProvider CreateTestServiceProvider(string pipelineFileName)
        {
            var configuration = new ConfigurationBuilder()
             .AddInMemoryCollection(new Dictionary<string, string?>
             {
                 ["AiEngine:DefaultPipelineDefinitionSource"] = "Json",
                 ["AiEngine:JsonPipelineDefinitionFilePath"] = $"config/{pipelineFileName}",

                 ["AiEngine:PayloadStore:Enabled"] = "true",
                 ["AiEngine:PayloadStore:Provider"] = "mongo-redis",
                 ["AiEngine:PayloadStore:RequireReplaySafePayloads"] = "true",
                 ["AiEngine:PayloadStore:MaxInlineSizeBytes"] = "512",

                 ["AiEngine:PayloadStore:Mongo:Enabled"] = "true",
                 ["AiEngine:PayloadStore:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                 ["AiEngine:PayloadStore:Mongo:DatabaseName"] = "multiplexed_ai_tests",
                 ["AiEngine:PayloadStore:Mongo:CollectionName"] = "payloads_multi_worker_retry_tests",

                 ["AiEngine:PayloadStore:RedisCache:Enabled"] = "true",
                 ["AiEngine:PayloadStore:RedisCache:KeyPrefix"] = "test:ai:payload:multi-worker-retry",
                 ["AiEngine:PayloadStore:RedisCache:ExpirationSeconds"] = "120",
                 ["AiEngine:PayloadStore:RedisCache:MaxCacheablePayloadBytes"] = "1048576",

                 ["AiEngine:PayloadStore:StepIndexCache:Enabled"] = "true",
                 ["AiEngine:PayloadStore:StepIndexCache:KeyPrefix"] = "test:ai:step-index:multi-worker-retry",
                 ["AiEngine:PayloadStore:StepIndexCache:ExpirationSeconds"] = "120",
                 ["AiEngine:PayloadStore:StepIndexCache:RefreshTtlOnRead"] = "true",

                 ["AiExecutionCleanup:AutoCleanupOnCompleted"] = "false",
                 ["AiExecutionCleanup:AutoCleanupOnFailed"] = "false",
                 ["AiExecutionCleanup:SuppressCleanupExceptions"] = "true"
             })
             .Build();

            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);

            services.AddMemoryCache();
            services.AddOptions();

            services.AddSingleton<IAiExecutionStateWriter, DefaultAiExecutionStateWriter>();
            services.AddSingleton<IAiExecutionStateReader>(sp =>
                new DefaultAiExecutionStateReader(new NoopPayloadResolver()));

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
            });

            services.AddSingleton<IConnectionMultiplexer>(_connection);

            // Already available in your debug/test toolbox.
            services.AddSingleton<TestStepAttemptTracker>();

            services.AddSingleton<ExecutionContextAccessor>();
            services.AddSingleton<IExecutionContextAccessor>(sp => sp.GetRequiredService<ExecutionContextAccessor>());
            services.AddSingleton<IExecutionContextFactory, ExecutionContextFactory>();

            var contextOptions = Options.Create(new ContextRuntimeOptions
            {
                UseRedisLuaScriptShaCaching = true
            });

            services.AddSingleton(contextOptions);

            services.AddSingleton<IContextStore>(sp =>
            {
                var redisContextStore = new RedisContextStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    contextOptions);

                var memoryContextStore = new MemoryContextStore(
                    new MemoryCache(new MemoryCacheOptions()),
                    TimeSpan.FromMinutes(5));

                return new CompositeContextStore(redisContextStore, memoryContextStore);
            });

            services.AddMultiplexAI(configuration);
            services.AddAiExecutionReplay();

            services.AddMultiplexRealtime()
                .AddSignalRRealtimeTransport(options =>
                {
                    options.CorsPolicy = "SignalRCors";
                    options.AllowedOrigins =
                    [
                        "http://localhost:3000"
                    ];
                });

            // Registers the control-plane identity resolver before signal services.
            services.AddAiControlPlaneDiscoveryCore();
            services.AddAiRuntimeSignals();

            services.AddAiStepsFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(AiDagExecutionEngineRedisRetryBudgetTests).Assembly);

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Creates a deterministic runtime RBAC context for integration tests.
        /// </summary>
        private static ExecutionContext CreateRuntimeContext()
        {
            return new ExecutionContext
            {
                ContextKey = string.Empty,
                Project = "Project",
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "Namespace",
                UserId = "userId",
                Namespaces = new List<NamespaceEntry>
                {
                    new NamespaceEntry
                    {
                        Name = "Namespace",
                        Trns = new HashSet<string>
                        {
                            "trn:Project:crm:billing:invoice:read",
                            "trn:Project:crm:billing:invoice:refund"
                        }
                    }
                },
                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Deletes the Redis execution bundle created by the test.
        /// </summary>
        private async Task CleanupDagExecutionAsync(string executionId)
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
}