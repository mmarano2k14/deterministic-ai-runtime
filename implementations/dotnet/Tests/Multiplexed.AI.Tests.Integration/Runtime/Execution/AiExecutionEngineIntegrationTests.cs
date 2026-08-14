using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Runtime.Execution.Cleanup;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.State;
using Multiplexed.AI.Runtime.Pipeline.Steps;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Stores.Cache;
using Multiplexed.AI.Stores.Cache.Redis;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Infrastructure;
using Multiplexed.AI.Tests.Integration.Models;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores;
using Multiplexed.Rbac.Core.Stores.Cache;
using Multiplexed.Rbac.Core.Stores.Memory;
using StackExchange.Redis;
using Xunit;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution
{
    /// <summary>
    /// End-to-end integration tests for <see cref="AiSequentialExecutionEngine"/>.
    ///
    /// PURPOSE:
    /// - Validate persisted sequential orchestration.
    /// - Validate execution creation and step progression.
    /// - Validate result persistence and terminal behavior.
    /// - Validate fake and real RBAC context-store integration.
    ///
    /// ARCHITECTURE:
    /// - <see cref="AiExecutionState"/> is treated as a persistence model.
    /// - State mutation is routed through <see cref="IAiExecutionStateWriter"/>.
    /// - State reading is routed through <see cref="IAiExecutionStateReader"/>.
    /// </summary>
    [Collection("redis")]
    public sealed class AiExecutionEngineIntegrationTests
    {
        private readonly IConnectionMultiplexer _connection;

        public AiExecutionEngineIntegrationTests(RedisFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            _connection = fixture.Connection;
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Run_Full_Pipeline_And_Complete()
        {
            const string pipelineName = "pipeline-full";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = "Marco" }),
                new TestStepDefinition(
                    Name: "summary",
                    StepKey: "summary",
                    Order: 2));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Marco",
                CancellationToken.None);

            try
            {
                var afterStep1 = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Running, afterStep1.Status);
                Assert.Equal(1, afterStep1.CurrentStepIndex);
                Assert.Equal("summary", afterStep1.CurrentStep);
                Assert.Contains("hello", afterStep1.CompletedSteps);

                var persistedAfterStep1State = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persistedAfterStep1State);

                var helloResult = persistedAfterStep1State!.Steps["hello"].Result;
                Assert.Equal("Hello World : Marco", helloResult?.Output);

                var finalRecord = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                Assert.Equal(2, finalRecord.CompletedSteps.Count);
                Assert.Contains("hello", finalRecord.CompletedSteps);
                Assert.Contains("summary", finalRecord.CompletedSteps);

                var persistedFinalState = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persistedFinalState);

                var summarized = persistedFinalState!.Steps["summary"]?.Result?.Output;

                Assert.False(string.IsNullOrWhiteSpace(summarized));
                Assert.Contains("Summarize the following content", summarized);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Run_Full_Pipeline_And_Complete_With_Real_ContextStore()
        {
            const string pipelineName = "pipeline-full-real-context";

            var engine = CreateEngineWithRealContextStore(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = "Marco" }),
                new TestStepDefinition(
                    Name: "summary",
                    StepKey: "summary",
                    Order: 2));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Marco",
                CancellationToken.None);

            try
            {
                var afterStep1 = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Running, afterStep1.Status);
                Assert.Equal(1, afterStep1.CurrentStepIndex);
                Assert.Equal("summary", afterStep1.CurrentStep);
                Assert.Contains("hello", afterStep1.CompletedSteps);

                var persistedAfterStep1State = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persistedAfterStep1State);

                var helloResult = persistedAfterStep1State!.Steps["hello"].Result;
                Assert.Equal("Hello World : Marco", helloResult?.Output);

                var finalRecord = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                Assert.Equal(2, finalRecord.CompletedSteps.Count);
                Assert.Contains("hello", finalRecord.CompletedSteps);
                Assert.Contains("summary", finalRecord.CompletedSteps);

                var persistedFinalState = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persistedFinalState);

                var summarized = persistedFinalState!.Steps["summary"]?.Result?.Output;

                Assert.False(string.IsNullOrWhiteSpace(summarized));
                Assert.Contains("Summarize the following content", summarized);
            }
            finally
            {
                await CleanupExecutionAndContextAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteAllAsync_Should_Run_Remaining_Pipeline_To_Completion()
        {
            const string pipelineName = "pipeline-execute-all";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = "Bangkok 2" }),
                new TestStepDefinition(
                    Name: "summary",
                    StepKey: "summary",
                    Order: 2));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Bangkok",
                CancellationToken.None);

            try
            {
                var finalRecord = await engine.ExecuteAllAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                Assert.Equal(2, finalRecord.CompletedSteps.Count);

                var finalState = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(finalState);

                var helloResult = finalState!.Steps["hello"].Result;
                Assert.Equal("Hello World : Bangkok 2", helloResult?.Output);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteAllAsync_Should_Run_To_Completion_With_Real_ContextStore()
        {
            const string pipelineName = "pipeline-execute-all-real-context";

            var engine = CreateEngineWithRealContextStore(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = "Phuket" }),
                new TestStepDefinition(
                    Name: "summary",
                    StepKey: "summary",
                    Order: 2));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Phuket",
                CancellationToken.None);

            try
            {
                var finalRecord = await engine.ExecuteAllAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, finalRecord.Status);
                Assert.Equal(2, finalRecord.CompletedSteps.Count);

                var finalState = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(finalState);

                var helloResult = finalState!.Steps["hello"].Result;
                Assert.Equal("Hello World : Phuket", helloResult?.Output);

                var reader = GetRequiredService<IAiExecutionStateReader>(engine);

                var summary = await reader.GetDataAsync<string>(
                    finalState,
                    AiExecutionKeys.Input,
                    CancellationToken.None);

                Assert.False(string.IsNullOrWhiteSpace(summary));
            }
            finally
            {
                await CleanupExecutionAndContextAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Mark_Execution_As_Failed_When_Step_Returns_Failed_Result()
        {
            const string pipelineName = "pipeline-fail-result";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "fail",
                    StepKey: "always-fail",
                    Order: 1));

            var execution = await engine.CreateAsync(
                pipelineName,
                "input",
                CancellationToken.None);

            try
            {
                var failedRecord = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Failed, failedRecord.Status);
                Assert.Empty(failedRecord.CompletedSteps);

                var persisted = await GetStore()
                    .GetRecordAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persisted);
                Assert.Equal(AiExecutionStatus.Failed, persisted!.Status);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Mark_Execution_As_Failed_When_Step_Throws()
        {
            const string pipelineName = "pipeline-throw";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "throw",
                    StepKey: "throwing-step",
                    Order: 1));

            var execution = await engine.CreateAsync(
                pipelineName,
                "input",
                CancellationToken.None);

            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.ExecuteNextAsync(execution.ExecutionId, CancellationToken.None));

                var persisted = await GetStore()
                    .GetRecordAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(persisted);
                Assert.Equal(AiExecutionStatus.Failed, persisted!.Status);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Return_Terminal_Record_When_Already_Completed()
        {
            const string pipelineName = "pipeline-terminal";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = AiExecutionKeys.Input }));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Phuket",
                CancellationToken.None);

            try
            {
                var completed = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, completed.Status);

                var again = await engine.ExecuteNextAsync(
                    execution.ExecutionId,
                    CancellationToken.None);

                Assert.Equal(AiExecutionStatus.Completed, again.Status);
                Assert.Equal(completed.ExecutionId, again.ExecutionId);
                Assert.Equal(completed.Version, again.Version);
                Assert.Equal(completed.ExecutionStepKey, again.ExecutionStepKey);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        [RedisFact]
        public async Task ExecuteNextAsync_Should_Keep_Record_And_State_Aligned_Across_Persistence()
        {
            const string pipelineName = "pipeline-alignment";

            var engine = CreateEngine(
                pipelineName,
                new TestStepDefinition(
                    Name: "hello",
                    StepKey: "hello-world",
                    Order: 1,
                    Input: new Dictionary<string, object?> { ["text"] = "Sage" }),
                new TestStepDefinition(
                    Name: "summary",
                    StepKey: "summary",
                    Order: 2));

            var execution = await engine.CreateAsync(
                pipelineName,
                "Sage",
                CancellationToken.None);

            try
            {
                await engine.ExecuteNextAsync(execution.ExecutionId, CancellationToken.None);
                await engine.ExecuteNextAsync(execution.ExecutionId, CancellationToken.None);

                var storedRecord = await GetStore()
                    .GetRecordAsync(execution.ExecutionId, CancellationToken.None);

                var storedState = await GetStore()
                    .GetStateAsync(execution.ExecutionId, CancellationToken.None);

                Assert.NotNull(storedRecord);
                Assert.NotNull(storedState);

                Assert.Equal(storedRecord!.ExecutionId, storedState!.ExecutionId);
                Assert.Equal(storedRecord.PipelineName, storedState.PipelineName);
                Assert.Equal(AiExecutionStatus.Completed, storedRecord.Status);

                var helloResult = storedState.Steps["hello"].Result;
                Assert.Equal("Hello World : Sage", helloResult?.Output);
            }
            finally
            {
                await CleanupExecutionAsync(execution.ExecutionId);
            }
        }

        private AiSequentialExecutionEngine CreateEngine(
            string pipelineName,
            params TestStepDefinition[] steps)
        {
            var store = GetStore();
            var contextStore = new FakeContextStore();
            var accessor = new ExecutionContextAccessor();
            var contextFactory = new ExecutionContextFactory();

            var services = new ServiceCollection();

            services.AddSingleton<IAiExecutionStateWriter, DefaultAiExecutionStateWriter>();
            services.AddSingleton<IAiExecutionStateReader>(_ =>
                new DefaultAiExecutionStateReader(new NoopPayloadResolver()));

            var provider = services.BuildServiceProvider();

            var pipelineExecutor = new FakeAiPipelineExecutor(pipelineName, steps);
            var logger = new NoopLogger();

            accessor.Set(BuildTestExecutionContext());

            var cleanupService = new NoOpAiExecutionCleanupService();

            var cleanupOptions = Options.Create(new AiExecutionCleanupOptions
            {
                AutoCleanupOnCompleted = false,
                AutoCleanupOnFailed = false,
                SuppressCleanupExceptions = true
            });

            return new AiSequentialExecutionEngine(
                store,
                contextStore,
                accessor,
                contextFactory,
                provider,
                pipelineExecutor,
                logger,
                cleanupService,
                cleanupOptions,
                provider.GetRequiredService<IAiExecutionStateReader>(),
                provider.GetRequiredService<IAiExecutionStateWriter>());
        }

        private AiSequentialExecutionEngine CreateEngineWithRealContextStore(
            string pipelineName,
            params TestStepDefinition[] steps)
        {
            var store = GetStore();
            var contextStore = CreateRealContextStore();
            var accessor = new ExecutionContextAccessor();
            var contextFactory = new ExecutionContextFactory();

            var services = new ServiceCollection();

            services.AddSingleton<IAiExecutionStateWriter, DefaultAiExecutionStateWriter>();
            services.AddSingleton<IAiExecutionStateReader>(_ =>
                new DefaultAiExecutionStateReader(new NoopPayloadResolver()));

            var provider = services.BuildServiceProvider();

            var pipelineExecutor = new FakeAiPipelineExecutor(pipelineName, steps);
            var logger = new NoopLogger();

            accessor.Set(BuildTestExecutionContext());

            var cleanupService = new NoOpAiExecutionCleanupService();

            var cleanupOptions = Options.Create(new AiExecutionCleanupOptions
            {
                AutoCleanupOnCompleted = false,
                AutoCleanupOnFailed = false,
                SuppressCleanupExceptions = true
            });

            return new AiSequentialExecutionEngine(
                store,
                contextStore,
                accessor,
                contextFactory,
                provider,
                pipelineExecutor,
                logger,
                cleanupService,
                cleanupOptions,
                provider.GetRequiredService<IAiExecutionStateReader>(),
                provider.GetRequiredService<IAiExecutionStateWriter>());
        }

        private IAiExecutionStore GetStore()
        {
            var keyBuilder = new AiExecutionKeyBuilder();
            var redis = new RedisAiExecutionStore(_connection, keyBuilder);
            var memory = new MemoryAiExecutionStore();

            return new AiExecutionStore(redis, memory);
        }

        private IContextStore CreateRealContextStore()
        {
            var options = Options.Create(new ContextRuntimeOptions
            {
                UseRedisLuaScriptShaCaching = true
            });

            var cache = new MemoryCache(new MemoryCacheOptions());
            var ttl = TimeSpan.FromMinutes(10);

            var redis = new RedisContextStore(_connection, options);
            var memory = new MemoryContextStore(cache, ttl);

            return new CompositeContextStore(redis, memory);
        }

        private static ExecutionContext BuildTestExecutionContext()
        {
            return new ExecutionContext
            {
                ContextKey = "ctx_seed_test",
                Project = "Project",
                TenantId = "tenant-id-xxxx",
                TenantGroupId = "tenant-group-id-xxx",
                CurrentNamespace = "Namespace",
                UserId = "user-id",
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

        private async Task CleanupExecutionAsync(string executionId)
        {
            var db = _connection.GetDatabase();

            await db.KeyDeleteAsync(GetExecutionRecordKey(executionId));
            await db.KeyDeleteAsync(GetExecutionStateKey(executionId));
        }

        private async Task CleanupExecutionAndContextAsync(string executionId)
        {
            var store = GetStore();
            var record = await store.GetRecordAsync(executionId, CancellationToken.None);

            var currentContextKey = record?.ContextKey;

            await CleanupExecutionAsync(executionId);

            if (!string.IsNullOrWhiteSpace(currentContextKey))
            {
                await CleanupContextAsync(currentContextKey);
            }

            await CleanupContextAsync("ctx_seed_test");
        }

        private async Task CleanupContextAsync(string contextKey)
        {
            var db = _connection.GetDatabase();

            await db.KeyDeleteAsync(GetRbacContextKey(contextKey));
            await db.KeyDeleteAsync(GetRbacInFlightKey(contextKey));
        }

        private static string GetExecutionRecordKey(string executionId)
            => $"ai:execution:record:{executionId}";

        private static string GetExecutionStateKey(string executionId)
            => $"ai:execution:state:{executionId}";

        private static string GetRbacContextKey(string contextKey)
            => $"ac:ctx:{contextKey}";

        private static string GetRbacInFlightKey(string contextKey)
            => $"ac:inflight:{contextKey}";

        private sealed record TestStepDefinition(
            string Name,
            string StepKey,
            int Order,
            IReadOnlyDictionary<string, object?>? Input = null,
            IReadOnlyDictionary<string, object?>? Config = null);

        private sealed class FakeAiPipelineExecutor : IAiSequentialPipelineExecutor
        {
            private readonly string _pipelineName;
            private readonly IReadOnlyList<TestStepDefinition> _steps;

            public FakeAiPipelineExecutor(
                string pipelineName,
                IEnumerable<TestStepDefinition> steps)
            {
                _pipelineName = pipelineName;
                _steps = steps.OrderBy(x => x.Order).ToArray();
            }

            public Task<ResolvedAiPipeline> PrepareAsync(
                string pipelineName,
                CancellationToken cancellationToken = default)
            {
                if (!string.Equals(_pipelineName, pipelineName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unknown pipeline '{pipelineName}'.");
                }

                var resolved = new ResolvedAiPipeline
                {
                    Name = pipelineName,
                    Version = "1",
                    Steps = _steps
                        .Select(x => new ResolvedAiPipelineStep
                        {
                            Name = x.Name,
                            StepKey = x.StepKey,
                            Order = x.Order,
                            Step = ResolveStep(x.StepKey),
                            Input = x.Input ?? new Dictionary<string, object?>(),
                            Config = x.Config ?? new Dictionary<string, object?>()
                        })
                        .ToArray()
                };

                return Task.FromResult(resolved);
            }

            public Task<ResolvedAiPipeline> PrepareAsync(
                AiPipelineDefinition definition,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);

                return PrepareAsync(definition.Name, cancellationToken);
            }

            public async Task<PipelineExecutionResult> ExecuteNextAsync(
                ResolvedAiPipeline pipeline,
                AiExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                var stateWriter = context.GetRequiredService<IAiExecutionStateWriter>();

                var orderedSteps = pipeline.Steps
                    .OrderBy(x => x.Order)
                    .ToArray();

                var currentIndex = context.Record.CurrentStepIndex;

                if (currentIndex < 0 || currentIndex >= orderedSteps.Length)
                {
                    throw new InvalidOperationException(
                        $"Execution step index '{currentIndex}' is outside the pipeline bounds.");
                }

                var currentStep = orderedSteps[currentIndex];

                stateWriter.EnsureStepInitialized(context.State, currentStep);

                var stepContext = new AiStepExecutionContext(
                    context,
                    currentStep);

                var result = await currentStep.Step.ExecuteAsync(
                    stepContext,
                    cancellationToken);

                stateWriter.SetStepResult(
                    context.State,
                    currentStep.Name,
                    result);

                var nextStepIndex = currentIndex + 1;
                var isCompleted = nextStepIndex >= orderedSteps.Length;

                return new PipelineExecutionResult
                {
                    PipelineName = pipeline.Name,
                    Steps = orderedSteps.Select(x => x.Name).ToArray(),
                    ExecutedStepName = currentStep.Name,
                    ExecutedStepIndex = currentIndex,
                    NextStepIndex = nextStepIndex,
                    NextStepName = isCompleted ? null : orderedSteps[nextStepIndex].Name,
                    IsCompleted = isCompleted,
                    StepResult = result
                };
            }

            private static IAiStep ResolveStep(string stepKey)
            {
                return stepKey switch
                {
                    "hello-world" => new HelloWorldStep(),
                    "summary" => new SummaryStep(new FakeAiService()),
                    "always-fail" => new AlwaysFailStep(),
                    "throwing-step" => new ThrowingStep(),
                    _ => throw new InvalidOperationException($"Unknown step key '{stepKey}'.")
                };
            }
        }

        private sealed class FakeAiService : IAiService
        {
            public Task<AiResponse> CompleteAsync(
                AiRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AiResponse
                {
                    Content = $"SUMMARY::{request.Prompt}"
                });
            }
        }

        [AiStep("always-fail-fake")]
        private sealed class AlwaysFailStep : IAiStep
        {
            public string Name => "always-fail-fake";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiStepResult.Fail("Forced failure."));
            }
        }

        [AiStep("throwing-step")]
        private sealed class ThrowingStep : IAiStep
        {
            public string Name => "throwing-step";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Forced exception.");
            }
        }

        private sealed class NoopPayloadResolver : IAiExecutionPayloadResolver
        {
            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Payload resolution is not expected in this test.");
            }
        }

        private static T GetRequiredService<T>(AiSequentialExecutionEngine engine)
            where T : notnull
        {
            var property = typeof(AiExecutionEngine)
                .GetProperty(
                    "Services",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

            if (property?.GetValue(engine) is not IServiceProvider provider)
            {
                throw new InvalidOperationException(
                    "Unable to access engine services.");
            }

            return provider.GetRequiredService<T>();
        }
    }
}