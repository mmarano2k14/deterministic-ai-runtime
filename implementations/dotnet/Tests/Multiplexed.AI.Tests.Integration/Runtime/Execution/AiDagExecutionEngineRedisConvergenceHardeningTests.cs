using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.DI;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.DI.Persistence;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Discovery;
using Multiplexed.AI.Runtime.ControlPlane.Signals;
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
    /// Distributed DAG convergence and finalization hardening tests.
    ///
    /// PURPOSE:
    /// - Validate deterministic global execution projection.
    /// - Verify retry-aware waiting states are not finalized too early.
    /// - Verify terminal failure is projected only after all progress paths are exhausted.
    /// - Verify no active or retryable work remains after terminal convergence.
    ///
    /// ARCHITECTURE:
    /// - <see cref="AiExecutionState"/> is treated as a persistence model.
    /// - Step-state mutation is routed through <see cref="IAiExecutionStateWriter"/>.
    /// - Redis remains the distributed source for persisted DAG state.
    /// </summary>
    [Collection("redis")]
    public sealed class AiDagExecutionEngineRedisConvergenceHardeningTests
    {
        private readonly IConnectionMultiplexer _connection;

        /// <summary>
        /// Initializes a new instance of the test suite with the shared Redis fixture.
        /// </summary>
        public AiDagExecutionEngineRedisConvergenceHardeningTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);

            _connection = fixture.Connection;
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Not_Finalize_While_Step_Is_WaitingForRetry()
        {
            var provider = CreateTestServiceProvider("multi-worker-retry-hardcore.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "multi-worker-retry-hardcore",
                input: "test");

            try
            {
                try
                {
                    await engine.ExecuteNextAsync(record.ExecutionId);
                }
                catch
                {
                    // Expected: the test step intentionally fails and moves into retry waiting.
                }

                var persistedRecord = await dagStore.GetRecordAsync(record.ExecutionId);
                var persistedState = await dagStore.GetStateAsync(record.ExecutionId);

                Assert.NotNull(persistedRecord);
                Assert.NotNull(persistedState);

                var step = persistedState!.Steps["start"];

                Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
                Assert.Equal(AiExecutionStatus.Waiting, persistedRecord!.Status);

                Assert.False(persistedRecord.IsTerminal);
                Assert.True(step.RetryState?.NextRetryAtUtc.HasValue);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Converge_To_Failed_Only_When_No_Progress_Remains()
        {
            var provider = CreateTestServiceProvider("multi-worker-retry-hardcore.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "multi-worker-retry-hardcore",
                input: "test");

            try
            {
                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        await engine.ExecuteNextAsync(record.ExecutionId);
                    }
                    catch
                    {
                        // Expected: the step consumes retry budget progressively.
                    }

                    var state = await dagStore.GetStateAsync(record.ExecutionId);
                    Assert.NotNull(state);

                    var step = stateWriter.GetOrCreateStep(state!, "start");

                    if (step.Status == AiStepExecutionStatus.WaitingForRetry)
                    {
                        await WaitUntilRetryWindowOpensAsync(
                            dagStore,
                            stateWriter,
                            record.ExecutionId,
                            "start");
                    }
                }

                var finalRecord = await WaitForTerminalRecordAsync(engine, record.ExecutionId);
                var finalState = await dagStore.GetStateAsync(record.ExecutionId);

                Assert.NotNull(finalState);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord.Status);
                Assert.Equal(AiStepExecutionStatus.Failed, finalState!.Steps["start"].Status);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Not_Leave_Active_Work_After_Terminal_Failure()
        {
            var provider = CreateTestServiceProvider("multi-worker-retry-hardcore.json");

            var engine = provider.GetRequiredService<AiDagExecutionEngine>();
            var dagStore = provider.GetRequiredService<IAiDagExecutionStore>();
            var accessor = provider.GetRequiredService<ExecutionContextAccessor>();
            var stateWriter = provider.GetRequiredService<IAiExecutionStateWriter>();

            accessor.Set(CreateRuntimeContext());

            var record = await engine.CreateAsync(
                pipelineName: "multi-worker-retry-hardcore",
                input: "test");

            try
            {
                var terminal = await WaitForTerminalFailureAsync(
                    engine,
                    dagStore,
                    stateWriter,
                    record.ExecutionId);

                var finalState = await dagStore.GetStateAsync(record.ExecutionId);
                var finalRecord = await dagStore.GetRecordAsync(record.ExecutionId);

                Assert.NotNull(finalState);
                Assert.NotNull(finalRecord);

                Assert.Equal(AiExecutionStatus.Failed, terminal.Status);
                Assert.Equal(AiExecutionStatus.Failed, finalRecord!.Status);

                Assert.DoesNotContain(
                    finalState!.Steps.Values,
                    x => x.Status == AiStepExecutionStatus.Running ||
                         x.Status == AiStepExecutionStatus.Ready ||
                         x.Status == AiStepExecutionStatus.WaitingForRetry ||
                         x.Status == AiStepExecutionStatus.None);
            }
            finally
            {
                await CleanupDagExecutionAsync(record.ExecutionId);
                await provider.DisposeAsync();
            }
        }

        /// <summary>
        /// Waits until a retry-delayed step becomes eligible for retry.
        ///
        /// IMPORTANT:
        /// - Polls persisted distributed state instead of relying on local assumptions.
        /// - Uses <see cref="IAiExecutionStateWriter"/> to access or initialize step state
        ///   consistently with the refactored state boundary.
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
        /// Repeatedly advances the engine until the global execution record reaches
        /// a terminal state.
        ///
        /// NOTE:
        /// - Distributed step state is the source of truth.
        /// - The global record is a projected summary and may lag briefly during races.
        /// </summary>
        private static async Task<AiExecutionRecord> WaitForTerminalRecordAsync(
            AiDagExecutionEngine engine,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 20; i++)
            {
                var record = await engine.ExecuteNextAsync(executionId, cancellationToken);

                if (record.IsTerminal)
                {
                    return record;
                }

                await Task.Delay(50, cancellationToken);
            }

            throw new TimeoutException(
                $"Execution '{executionId}' did not converge to a terminal record in time.");
        }

        /// <summary>
        /// Drives the retry-only pipeline until it reaches terminal failure.
        ///
        /// PURPOSE:
        /// - Preserve the real distributed retry flow.
        /// - Wait for retry windows when retry delay is active.
        /// - Stop only when the projected global record becomes terminal.
        /// </summary>
        private static async Task<AiExecutionRecord> WaitForTerminalFailureAsync(
            AiDagExecutionEngine engine,
            IAiDagExecutionStore dagStore,
            IAiExecutionStateWriter stateWriter,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    var record = await engine.ExecuteNextAsync(executionId, cancellationToken);

                    if (record.IsTerminal)
                    {
                        return record;
                    }
                }
                catch
                {
                    // Expected: the retry test step intentionally fails during retry progression.
                }

                var state = await dagStore.GetStateAsync(executionId, cancellationToken);
                if (state is null)
                {
                    throw new InvalidOperationException($"State '{executionId}' was not found.");
                }

                var step = stateWriter.GetOrCreateStep(state, "start");

                if (step.Status == AiStepExecutionStatus.WaitingForRetry)
                {
                    await WaitUntilRetryWindowOpensAsync(
                        dagStore,
                        stateWriter,
                        executionId,
                        "start",
                        cancellationToken);
                }
            }

            throw new TimeoutException(
                $"Execution '{executionId}' did not converge to terminal failure in time.");
        }

        /// <summary>
        /// Creates a test service provider using real runtime DI and the Redis fixture.
        /// </summary>
        private ServiceProvider CreateTestServiceProvider(
            string pipelineFileName)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiEngine:DefaultPipelineDefinitionSource"] = "Json",
                    ["AiEngine:JsonPipelineDefinitionFilePath"] =
                        $"config/{pipelineFileName}",

                    // Payload store
                    ["AiEngine:PayloadStore:Enabled"] = "true",
                    ["AiEngine:PayloadStore:Provider"] = "mongo-redis",
                    ["AiEngine:PayloadStore:MaxInlineSizeBytes"] = "512",

                    ["AiEngine:PayloadStore:Mongo:Enabled"] = "true",
                    ["AiEngine:PayloadStore:Mongo:ConnectionString"] =
                        "mongodb://localhost:27017",
                    ["AiEngine:PayloadStore:Mongo:DatabaseName"] =
                        "multiplexed_ai_tests",
                    ["AiEngine:PayloadStore:Mongo:CollectionName"] =
                        "payloads_redis_convergence_tests",

                    ["AiEngine:PayloadStore:RedisCache:Enabled"] = "true",
                    ["AiEngine:PayloadStore:RedisCache:KeyPrefix"] =
                        "test:ai:payload:redis",
                    ["AiEngine:PayloadStore:RedisCache:ExpirationSeconds"] = "120",

                    ["AiEngine:PayloadStore:StepIndexCache:Enabled"] = "true",
                    ["AiEngine:PayloadStore:StepIndexCache:KeyPrefix"] =
                        "test:ai:step-index:redis",
                    ["AiEngine:PayloadStore:StepIndexCache:ExpirationSeconds"] = "120",

                    // Cleanup
                    ["AiExecutionCleanup:AutoCleanupOnCompleted"] = "false",
                    ["AiExecutionCleanup:AutoCleanupOnFailed"] = "false",
                    ["AiExecutionCleanup:SuppressCleanupExceptions"] = "true"
                })
                .Build();

            var services = new ServiceCollection();

            // Required by DefaultAiControlPlaneIdResolver and other runtime services.
            services.AddSingleton<IConfiguration>(
                configuration);

            services.AddMemoryCache();
            services.AddOptions();

            services.AddLogging(builder =>
            {
                builder.ClearProviders();
            });

            services.AddSingleton<IConnectionMultiplexer>(
                _connection);

            services.AddSingleton<TestStepAttemptTracker>();

            services.AddSingleton<ExecutionContextAccessor>();

            services.AddSingleton<IExecutionContextAccessor>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<ExecutionContextAccessor>());

            services.AddSingleton<
                IExecutionContextFactory,
                ExecutionContextFactory>();

            var contextOptions = Options.Create(
                new ContextRuntimeOptions
                {
                    UseRedisLuaScriptShaCaching = true
                });

            services.AddSingleton(contextOptions);

            services.AddSingleton<IContextStore>(
                serviceProvider =>
                {
                    var redisContextStore = new RedisContextStore(
                        serviceProvider.GetRequiredService<IConnectionMultiplexer>(),
                        contextOptions);

                    var memoryContextStore = new MemoryContextStore(
                        new MemoryCache(
                            new MemoryCacheOptions()),
                        TimeSpan.FromMinutes(5));

                    return new CompositeContextStore(
                        redisContextStore,
                        memoryContextStore);
                });

            services.AddMultiplexAI(
                configuration);

            services.AddAiExecutionReplay();

            services
                .AddMultiplexRealtime()
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
                typeof(AiDagExecutionEngineRedisConvergenceHardeningTests).Assembly);

            return services.BuildServiceProvider();
        }

        /// <summary>
        /// Creates a deterministic RBAC runtime context for integration tests.
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
        /// Deletes all Redis keys created for one DAG execution.
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