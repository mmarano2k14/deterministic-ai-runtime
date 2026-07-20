using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
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
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// Distributed multi-worker retry claim tests for DAG execution.
    ///
    /// PURPOSE:
    /// Validates that retry-eligible work is reclaimed safely by only one worker
    /// when the retry window opens.
    ///
    /// THIS TEST SUITE PROVES:
    /// - only one worker may reclaim a retry-eligible step
    /// - a retry window is consumed atomically
    /// - RetryCount is not incremented more than once per scheduled retry
    /// - competing workers do not execute the same retry attempt concurrently
    /// </summary>
    [Collection("redis")]
    public sealed class AiDagExecutionEngineRedisMultiWorkerRetryHardeningTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineRedisMultiWorkerRetryTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared Redis fixture.</param>
        public AiDagExecutionEngineRedisMultiWorkerRetryHardeningTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            _connection = fixture.Connection;
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Allow_Only_One_Worker_To_Reclaim_When_Retry_Window_Opens()
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
                // First execution fails internally and schedules retry.
                // The runtime now converts thrown step exceptions into persisted failure/retry state.
                await engine.ExecuteNextAsync(record.ExecutionId);

                var stateAfterFailure = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(stateAfterFailure);

                var failedStep = stateAfterFailure!.Steps["start"];

                Assert.NotNull(failedStep.Retry);
                Assert.Equal(1, failedStep.Retry!.MaxRetries);
                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, failedStep.Status);
                Assert.Equal(1, failedStep.RetryState?.RetryCount);
                Assert.True(failedStep.RetryState?.NextRetryAtUtc.HasValue);

                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                // Two workers race to reclaim the same retry window.
                var worker1 = ExecuteNextCaptureAsync(engine, record.ExecutionId);
                var worker2 = ExecuteNextCaptureAsync(engine, record.ExecutionId);

                await Task.WhenAll(worker1, worker2);

                var results = new[] { worker1.Result, worker2.Result };

                // With runtime exception protection, ExecuteNextAsync should not throw.
                // The failure is persisted as DAG state instead.
                Assert.Equal(0, results.Count(x => x.Outcome == WorkerExecutionOutcome.Thrown));
                Assert.Equal(2, results.Count(x => x.Outcome == WorkerExecutionOutcome.Returned));

                var finalState = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(finalState);

                var finalStep = finalState!.Steps["start"];

                // With MaxRetries = 1, exactly one retry attempt should be consumed,
                // and the retry budget should now be exhausted.
                Assert.Equal(AiStepExecutionStatus.Failed, finalStep.Status);
                Assert.Equal(1, finalStep.RetryState?.RetryCount);
                Assert.Null(finalStep.RetryState?.NextRetryAtUtc);

                Assert.True(
                    string.IsNullOrWhiteSpace(finalStep.ClaimedBy) &&
                    string.IsNullOrWhiteSpace(finalStep.ClaimToken) &&
                    !finalStep.ClaimedAtUtc.HasValue,
                    "The failed step must not keep an active claim after retry exhaustion.");
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Not_Increment_RetryCount_More_Than_Once_For_The_Same_Retry_Window()
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
                // First execution fails and schedules retry #1.
                await engine.ExecuteNextAsync(record.ExecutionId);

                var stateAfterFirstFailure = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(stateAfterFirstFailure);

                var stepAfterFirstFailure = stateAfterFirstFailure!.Steps["start"];
                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, stepAfterFirstFailure.Status);
                Assert.Equal(1, stepAfterFirstFailure.RetryState?.RetryCount);

                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                // Competing workers try to consume the same retry window.
                var worker1 = ExecuteNextCaptureAsync(engine, record.ExecutionId);
                var worker2 = ExecuteNextCaptureAsync(engine, record.ExecutionId);

                await Task.WhenAll(worker1, worker2);

                var midState = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(midState);

                var midStep = midState!.Steps["start"];

                // The second retry should have been scheduled exactly once.
                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, midStep.Status);
                Assert.Equal(2, midStep.RetryState?.RetryCount);
                Assert.Equal(2, midStep!.Retry!.MaxRetries);
                Assert.True(midStep.RetryState?.NextRetryAtUtc.HasValue);

                // Ensure no double-consumption occurred.
                Assert.DoesNotContain(
                    new[] { worker1.Result, worker2.Result },
                    x => x.Outcome == WorkerExecutionOutcome.Thrown && midStep.RetryState?.RetryCount > 2);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Leave_At_Most_One_Active_Claim_During_Retry_Reclaim_Race()
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
                // First execution fails and schedules retry #1.
                await engine.ExecuteNextAsync(record.ExecutionId);

                var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

                await WaitUntilRetryWindowOpensAsync(
                    dagStore,
                    stateWriter,
                    record.ExecutionId,
                    "start");

                var tasks = Enumerable.Range(0, 8)
                    .Select(_ => ExecuteNextCaptureAsync(engine, record.ExecutionId))
                    .ToArray();

                await Task.WhenAll(tasks);

                var state = await dagStore.GetStateAsync(record.ExecutionId);
                Assert.NotNull(state);

                var step = state!.Steps["start"];

                // After the race, the step must not remain multiply claimed.
                // It may be:
                // - WaitingForRetry (retry #2 scheduled)
                // - Failed (if a later transition already exhausted it)
                Assert.True(
                    step.Status == AiStepExecutionStatus.WaitingForRetry ||
                    step.Status == AiStepExecutionStatus.Failed);

                var activeClaimFields =
                    (!string.IsNullOrWhiteSpace(step.ClaimedBy) ? 1 : 0) +
                    (!string.IsNullOrWhiteSpace(step.ClaimToken) ? 1 : 0) +
                    (step.ClaimedAtUtc.HasValue ? 1 : 0);

                Assert.True(activeClaimFields == 0 || activeClaimFields == 3);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        // ==============================
        // HELPERS
        // ==============================

        /// <summary>
        /// Waits until the retry window for a step becomes due.
        /// </summary>
        private static async Task WaitUntilRetryWindowOpensAsync(
             IAiDagExecutionStore dagStore,
             IAiExecutionStateWriter stateWriter,
             string executionId,
             string stepName,
             CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 100; i++)
            {
                var state = await dagStore.GetStateAsync(executionId, cancellationToken);
                if (state is null)
                {
                    throw new InvalidOperationException($"State '{executionId}' was not found.");
                }

                var step = stateWriter.GetOrCreateStep(state, stepName);

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

                var wait = delay < TimeSpan.FromMilliseconds(50)
                     ? delay
                     : TimeSpan.FromMilliseconds(50);

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

            }

            throw new TimeoutException(
                $"Retry window for step '{stepName}' in execution '{executionId}' did not open in time.");
        }

        /// <summary>
        /// Executes one engine pass and captures whether it returned or threw.
        /// </summary>
        private static async Task<WorkerExecutionResult> ExecuteNextCaptureAsync(
            AiDagExecutionEngine engine,
            string executionId)
        {
            try
            {
                var record = await engine.ExecuteNextAsync(executionId);
                return WorkerExecutionResult.Returned(record);
            }
            catch (Exception ex)
            {
                return WorkerExecutionResult.Thrown(ex);
            }
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

                // 🔥 AJOUT ICI
                ["AiEngine:PayloadStore:Enabled"] = "true",
                ["AiEngine:PayloadStore:Provider"] = "mongo-redis",
                ["AiEngine:PayloadStore:MaxInlineSizeBytes"] = "512",

                ["AiEngine:PayloadStore:Mongo:Enabled"] = "true",
                ["AiEngine:PayloadStore:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                ["AiEngine:PayloadStore:Mongo:DatabaseName"] = "multiplexed_ai_tests",
                ["AiEngine:PayloadStore:Mongo:CollectionName"] = "payloads_retry_tests",

                ["AiEngine:PayloadStore:RedisCache:Enabled"] = "true",
                ["AiEngine:PayloadStore:RedisCache:KeyPrefix"] = "test:ai:payload:retry",
                ["AiEngine:PayloadStore:RedisCache:ExpirationSeconds"] = "120",

                ["AiEngine:PayloadStore:StepIndexCache:Enabled"] = "true",
                ["AiEngine:PayloadStore:StepIndexCache:KeyPrefix"] = "test:ai:step-index:retry",
                ["AiEngine:PayloadStore:StepIndexCache:ExpirationSeconds"] = "120",

                // Cleanup
                ["AiExecutionCleanup:AutoCleanupOnCompleted"] = "false",
                ["AiExecutionCleanup:AutoCleanupOnFailed"] = "false",
                ["AiExecutionCleanup:SuppressCleanupExceptions"] = "true"
            })
            .Build();

            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);

            services.AddMemoryCache();
            services.AddOptions();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
            });

            services.AddSingleton<IConnectionMultiplexer>(_connection);

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
                typeof(AiDagExecutionEngineRedisMultiWorkerRetryTests).Assembly);

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

        private enum WorkerExecutionOutcome
        {
            Returned = 0,
            Thrown = 1
        }

        private sealed record WorkerExecutionResult(
            WorkerExecutionOutcome Outcome,
            AiExecutionRecord? Record,
            Exception? Exception)
        {
            public static WorkerExecutionResult Returned(AiExecutionRecord record)
                => new(WorkerExecutionOutcome.Returned, record, null);

            public static WorkerExecutionResult Thrown(Exception exception)
                => new(WorkerExecutionOutcome.Thrown, null, exception);
        }
    }
}