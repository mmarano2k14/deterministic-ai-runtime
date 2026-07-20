using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Retry;
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
    /// Integration tests validating the clean retry-state model used by DAG execution.
    ///
    /// PURPOSE:
    /// - Validate that retry configuration is persisted in AiStepState.Retry.
    /// - Validate that mutable retry execution data is stored in AiStepState.RetryState.
    /// - Validate that the executor no longer performs local retry loops.
    /// - Validate that Redis/Lua retry transitions remain deterministic and multi-worker safe.
    ///
    /// IMPORTANT:
    /// These tests intentionally avoid the obsolete StepState retry fields:
    /// RetryCount, MaxRetries, RetryDelayMs, and NextRetryAtUtc.
    /// </summary>
    [Collection("redis")]
    public sealed class AiDagRetryStateCleanIntegrationTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagRetryStateCleanIntegrationTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared Redis fixture.</param>
        public AiDagRetryStateCleanIntegrationTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            _connection = fixture.Connection;
        }

        /// <summary>
        /// Ensures a retrying DAG step eventually fails deterministically once the retry budget is exhausted.
        /// </summary>
        [RedisFact]
        public async Task ExecuteAllAsync_Should_Respect_RetryState_And_Fail_Deterministically()
        {
            var provider = CreateTestServiceProvider("dag-retry-clean.json");
            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync("dag-retry-clean", "Marco");

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    await ExecuteIgnoringFailureAsync(engine, created.ExecutionId);

                    var state = await dagStore.GetStateAsync(created.ExecutionId);
                    Assert.NotNull(state);

                    var step = stateWriter.GetOrCreateStep(state!, "retry-step");

                    Assert.NotNull(step.Retry);
                    Assert.NotNull(step.RetryState);

                    if (step.Status == AiStepExecutionStatus.WaitingForRetry)
                    {
                        await WaitForRetryWindowAsync(
                            dagStore,
                            stateWriter,
                            created.ExecutionId,
                            "retry-step");

                        continue;
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
                Assert.NotNull(finalStep.Retry);
                Assert.NotNull(finalStep.RetryState);
                Assert.Equal(finalStep.Retry!.MaxRetries, finalStep.RetryState!.RetryCount);
                Assert.Null(finalStep.RetryState.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        /// <summary>
        /// Ensures only one competing worker can consume an opened retry window.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Allow_Only_One_Worker_To_Consume_Retry_Window()
        {
            var provider = CreateTestServiceProvider("dag-retry-clean-maxretries-1.json");
            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync("dag-retry-clean-maxretries-1", "Marco");

            try
            {
                // First execution fails internally and schedules retry.
                // Runtime now converts thrown step exceptions into persisted retry state.
                await engine.ExecuteNextAsync(created.ExecutionId);

                var stateAfterFirstFailure = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(stateAfterFirstFailure);

                var stepAfterFirstFailure = stateWriter.GetOrCreateStep(stateAfterFirstFailure!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, stepAfterFirstFailure.Status);
                Assert.Equal(1, stepAfterFirstFailure.RetryState?.RetryCount);
                Assert.Equal(1, stepAfterFirstFailure.Retry?.MaxRetries);
                Assert.NotNull(stepAfterFirstFailure.RetryState?.NextRetryAtUtc);

                await WaitForRetryWindowAsync(
                    dagStore,
                    stateWriter,
                    created.ExecutionId,
                    "retry-step");

                var worker1 = ExecuteNextCaptureAsync(engine, created.ExecutionId);
                var worker2 = ExecuteNextCaptureAsync(engine, created.ExecutionId);

                await Task.WhenAll(worker1, worker2);

                var results = new[] { worker1.Result, worker2.Result };

                // With runtime exception protection, step failures are persisted as DAG state.
                // ExecuteNextAsync should no longer throw when the retry attempt fails.
                Assert.Equal(0, results.Count(x => x.Outcome == WorkerExecutionOutcome.Thrown));
                Assert.Equal(2, results.Count(x => x.Outcome == WorkerExecutionOutcome.Returned));

                var finalState = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(finalState);

                var finalStep = stateWriter.GetOrCreateStep(finalState!, "retry-step");

                // This is the real proof that only one retry window was consumed:
                // RetryCount remains 1 and the step is terminal Failed after max retry budget is exhausted.
                Assert.Equal(AiStepExecutionStatus.Failed, finalStep.Status);
                Assert.Equal(1, finalStep.Retry?.MaxRetries);
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
                await CleanupDagExecutionAsync(created.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        /// <summary>
        /// Ensures retry configuration is hydrated into the durable step state before Redis persistence.
        /// </summary>
        [RedisFact]
        public async Task CreateAsync_Should_Hydrate_Retry_Definition_Into_Step_State()
        {
            var provider = CreateTestServiceProvider("dag-retry-clean-policy.json");
            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync("dag-retry-clean-policy", "Marco");

            try
            {
                var state = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(state);

                var step = stateWriter.GetOrCreateStep(state!, "retry-step");

                Assert.NotNull(step.Retry);
                Assert.NotNull(step.RetryState);
                Assert.Equal(2, step.Retry!.MaxRetries);
                Assert.Equal(50, step.Retry.BaseDelayMs);
                Assert.Equal(AiRetryBackoffStrategy.Fixed, step.Retry.Strategy);
                Assert.Contains(
                    step.Retry.Policies,
                    policy => policy.Name == "retry.transient.default");
                Assert.Equal(0, step.RetryState!.RetryCount);
                Assert.Null(step.RetryState.NextRetryAtUtc);
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        /// <summary>
        /// Ensures a retry configuration using the policy-driven section schedules retry through RetryState only.
        /// </summary>
        [RedisFact]
        public async Task ExecuteNextAsync_Should_Use_Policy_Driven_Retry_Config_And_Runtime_RetryState()
        {
            var provider = CreateTestServiceProvider("dag-retry-clean-policy.json");
            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync("dag-retry-clean-policy", "Marco");

            try
            {
                await ExecuteIgnoringFailureAsync(engine, created.ExecutionId);

                var state = await dagStore.GetStateAsync(created.ExecutionId);
                Assert.NotNull(state);

                var step = stateWriter.GetOrCreateStep(state!, "retry-step");

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
                Assert.NotNull(step.Retry);
                Assert.NotNull(step.RetryState);
                Assert.Equal(2, step.Retry!.MaxRetries);
                Assert.Equal(1, step.RetryState!.RetryCount);
                Assert.NotNull(step.RetryState.NextRetryAtUtc);
                Assert.NotNull(step.RetryState.LastRetryAtUtc);
                Assert.False(string.IsNullOrWhiteSpace(step.RetryState.RetryReason));
            }
            finally
            {
                await CleanupDagExecutionAsync(created.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Executes one engine pass while intentionally swallowing step failures.
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
                // Expected for tests using intentionally failing steps.
            }
        }

        /// <summary>
        /// Waits until the retry window for a step is open or the step becomes terminal.
        /// </summary>
        private static async Task WaitForRetryWindowAsync(
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
        /// Executes one engine pass and captures whether the call returned or threw.
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

                    ["AiEngine:PayloadStore:Enabled"] = "true",
                    ["AiEngine:PayloadStore:Provider"] = "mongo-redis",
                    ["AiEngine:PayloadStore:MaxInlineSizeBytes"] = "512",

                    ["AiEngine:PayloadStore:Mongo:Enabled"] = "true",
                    ["AiEngine:PayloadStore:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                    ["AiEngine:PayloadStore:Mongo:DatabaseName"] = "multiplexed_ai_tests",
                    ["AiEngine:PayloadStore:Mongo:CollectionName"] = "payloads_retry_state_clean_tests",

                    ["AiEngine:PayloadStore:RedisCache:Enabled"] = "true",
                    ["AiEngine:PayloadStore:RedisCache:KeyPrefix"] = "test:ai:payload:retry-state-clean",
                    ["AiEngine:PayloadStore:RedisCache:ExpirationSeconds"] = "120",

                    ["AiEngine:PayloadStore:StepIndexCache:Enabled"] = "true",
                    ["AiEngine:PayloadStore:StepIndexCache:KeyPrefix"] = "test:ai:step-index:retry-state-clean",
                    ["AiEngine:PayloadStore:StepIndexCache:ExpirationSeconds"] = "120",

                    ["AiExecutionCleanup:AutoCleanupOnCompleted"] = "false",
                    ["AiExecutionCleanup:AutoCleanupOnFailed"] = "false",
                    ["AiExecutionCleanup:SuppressCleanupExceptions"] = "true"
                })
                .Build();

            var services = new ServiceCollection();

            // DefaultAiControlPlaneIdResolver depends on IConfiguration.
            services.AddSingleton<IConfiguration>(
                configuration);

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
                typeof(AiDagRetryStateCleanIntegrationTests).Assembly);

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
