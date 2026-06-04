using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.AI.Retry;
using Multiplexed.AI.Runtime.AI.Retry.Policies;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution;
using Multiplexed.AI.Runtime.Execution.Cleanup;
using Multiplexed.AI.Runtime.Execution.Engine;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay.Fingerprint;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay.Metadata;
using Multiplexed.AI.Runtime.Execution.Scheduling;
using Multiplexed.AI.Runtime.Execution.State;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Runtime.Pipeline.Definition;
using Multiplexed.AI.Runtime.Pipeline.Steps.Execution;
using Multiplexed.AI.Stores;
using Multiplexed.AI.Stores.Memory;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Integration.Fixtures;
using Multiplexed.AI.Tests.Integration.Helpers;
using Multiplexed.AI.Tests.Runtime.Execution.Instance;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;
using Multiplexed.Rbac.Core.Stores.Memory;
using StackExchange.Redis;
using Xunit;
using static Multiplexed.AI.Tests.Integration.Helpers.MetricsFactory;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Integration.Runtime.Execution.Engine
{
    /// <summary>
    /// Validates local retry scheduling behavior for DAG execution.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Validate local retry-engine behavior through the policy-driven retry engine.
    /// - Validate local retry scheduling after a transient failure.
    /// - Validate local retry recovery from a transient failure to a successful second attempt.
    ///
    /// RETRY:
    /// - Retry configuration is provided through step-level <c>config.retry</c>.
    /// - Retry policies are resolved through the shared policy engine.
    ///
    /// RETENTION MIGRATION:
    /// - This test no longer wires the legacy retention service.
    /// - This test no longer uses legacy retention options or retention modes.
    /// - Local retry behavior does not require retention.
    /// </remarks>
    public sealed class AiDagRetryEngineLocalIntegrationTests
    {
        /// <summary>
        /// Verifies that a local DAG step failure schedules retry using the policy-driven retry engine.
        /// </summary>
        [Fact]
        public async Task Local_Dag_Should_Schedule_Retry_With_Retry_Engine_When_Step_Fails()
        {
            var engine = CreateLocalEngine();

            var accessor = GetRequiredService<ExecutionContextAccessor>(engine);
            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync(
                "dag-retry-local",
                "hello");

            var record = await engine.ExecuteNextAsync(
                created.ExecutionId);

            var store = GetRequiredService<MemoryAiExecutionStore>(engine);

            var state = await store.GetStateAsync(
                created.ExecutionId,
                CancellationToken.None);

            Assert.NotNull(state);

            var step = state!.Steps["step-1"];

            Assert.NotNull(step.Retry);
            Assert.NotNull(step.RetryState);

            Assert.Equal(AiStepExecutionStatus.WaitingForRetry, step.Status);
            Assert.Equal(1, step.RetryState!.RetryCount);
            Assert.NotNull(step.RetryState.NextRetryAtUtc);

            Assert.True(
                record.Status == AiExecutionStatus.Running ||
                record.Status == AiExecutionStatus.Waiting,
                $"Unexpected execution status: {record.Status}");
        }

        /// <summary>
        /// Verifies that a local DAG step retries and succeeds on the second attempt.
        /// </summary>
        [Fact]
        public async Task Local_Dag_Should_Retry_And_Succeed_On_Second_Attempt()
        {
            TestFailThenSuccessStep.Reset();

            var engine = CreateLocalEngine(CreateLocalRetrySuccessPipeline());

            var accessor = GetRequiredService<ExecutionContextAccessor>(engine);
            accessor.Set(CreateRuntimeContext());

            var created = await engine.CreateAsync(
                "dag-retry-local-success",
                "hello");

            var store = GetRequiredService<MemoryAiExecutionStore>(engine);

            var firstRecord = await engine.ExecuteNextAsync(
                created.ExecutionId);

            var firstState = await store.GetStateAsync(
                created.ExecutionId,
                CancellationToken.None);

            Assert.NotNull(firstState);

            var firstStep = firstState!.Steps["step-1"];

            Assert.NotNull(firstStep.Retry);
            Assert.NotNull(firstStep.RetryState);
            Assert.Equal(AiStepExecutionStatus.WaitingForRetry, firstStep.Status);
            Assert.Equal(1, firstStep.RetryState!.RetryCount);
            Assert.NotNull(firstStep.RetryState.NextRetryAtUtc);

            Assert.True(
                firstRecord.Status == AiExecutionStatus.Running ||
                firstRecord.Status == AiExecutionStatus.Waiting,
                $"Unexpected execution status after first attempt: {firstRecord.Status}");

            var secondRecord = await engine.ExecuteNextAsync(
                created.ExecutionId);

            var secondState = await store.GetStateAsync(
                created.ExecutionId,
                CancellationToken.None);

            Assert.NotNull(secondState);

            var secondStep = secondState!.Steps["step-1"];

            Assert.Equal(AiStepExecutionStatus.Completed, secondStep.Status);
            Assert.NotNull(secondStep.RetryState);
            Assert.Equal(1, secondStep.RetryState!.RetryCount);
            Assert.Equal(AiExecutionStatus.Completed, secondRecord.Status);
        }

        /// <summary>
        /// Creates a local DAG engine with policy-driven retry wiring.
        /// </summary>
        private static AiDagExecutionEngine CreateLocalEngine(
            AiPipelineDefinition? pipeline = null)
        {
            var executionStore = new MemoryAiExecutionStore();

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var contextStore = new MemoryContextStore(
                memoryCache,
                TimeSpan.FromMinutes(5));

            var accessor = new ExecutionContextAccessor();
            var contextFactory = new ExecutionContextFactory();

            var logger = new NoopLogger();
            var stepExecutor = new AiStepExecutor(logger);

            var services = new ServiceCollection();

            services.AddAiStepsFromAssemblies(
                typeof(AiRuntimeAssemblyMarker).Assembly,
                typeof(AiDagRetryEngineLocalIntegrationTests).Assembly);

            var provider = services.BuildServiceProvider();

            var registry = provider.GetRequiredService<IAiStepRegistry>();
            var resolver = new AiPipelineResolver(registry);

            var sourceSelector = CreateInMemorySourceSelector(
                pipeline ?? CreateLocalRetryPipeline());

            var pipelineExecutor = new AiSequentialPipelineExecutor(
                sourceSelector,
                resolver,
                stepExecutor);

            var cleanupService = new NoOpAiExecutionCleanupService();

            var aiOptions = new AiEngineOptions
            {
                Cleanup = new AiExecutionCleanupOptions
                {
                    AutoCleanupOnCompleted = false,
                    AutoCleanupOnFailed = false,
                    SuppressCleanupExceptions = true
                }
            };

            var observability = ObservabilityFactory.Create();

            IAiExecutionStateWriter stateWriter = new DefaultAiExecutionStateWriter();

            IAiExecutionStateReader stateReader =
                new DefaultAiExecutionStateReader(new NoopPayloadResolver());

            var payloadCompactor = new NoopStepResultPayloadCompactor();
            var stepResolver = new LocalStateExecutionStepResolver();

            var retryPolicies = new IAiPolicy[]
            {
                new DefaultTransientRetryPolicy(),
                new DefaultTimeoutRetryPolicy(),
                new DefaultRateLimitRetryPolicy()
            };

            var policyRegistry = new DefaultAiPolicyRegistry(retryPolicies);

            var policyEngineRegistry = new DefaultAiPolicyEngineRegistry(
                new[]
                {
                    typeof(DefaultAiRetryEngine)
                });

            var policyFactory = new DefaultAiPolicyEngineFactory(
                policyRegistry,
                policyEngineRegistry,
                observability);

            var stepExecutionOrchestrator = new DefaultAiDagStepExecutionOrchestrator();

            var concurrencyGate = new NoOpAiConcurrencyGate();

            var runtimeInstanceIdentity = new TestAiRuntimeInstanceIdentity("test-runtime-instance");

            var executionControlGate = new NoOpAiExecutionControlGate();
            var executionControlService = new NoOpAiExecutionControlService();

            var replayMetadataStore =
                new InMemoryAiExecutionReplayMetadataStore();

            var replayFingerprintBuilder =
                new DefaultAiExecutionReplayFingerprintBuilder();

            var replayMetadataService =
                new DefaultAiExecutionReplayMetadataService(
                    replayFingerprintBuilder,
                    replayMetadataStore);

            var serviceProvider = new TestServiceProvider(new Dictionary<Type, object>
            {
                [typeof(ExecutionContextAccessor)] = accessor,
                [typeof(MemoryAiExecutionStore)] = executionStore,
                [typeof(IAiExecutionStore)] = executionStore,
                [typeof(IAiExecutionStateReader)] = stateReader,
                [typeof(IAiExecutionStateWriter)] = stateWriter,
                [typeof(IAiExecutionStepResolver)] = stepResolver,
                [typeof(IAiPolicyEngineFactory)] = policyFactory,
                [typeof(IAiDagStepExecutionOrchestrator)] = stepExecutionOrchestrator,
                [typeof(IAiConcurrencyGate)] = concurrencyGate,
                [typeof(IAiRuntimeInstanceIdentityDescriptor)] = runtimeInstanceIdentity,
                [typeof(IAiExecutionControlGate)] = executionControlGate,
                [typeof(IAiExecutionControlService)] = executionControlService,
                [typeof(IAiExecutionReplayMetadataStore)] = replayMetadataStore,
                [typeof(IAiExecutionReplayFingerprintBuilder)] = replayFingerprintBuilder,
                [typeof(IAiExecutionReplayMetadataService)] = replayMetadataService
            });

            var engineServices = new AiDagExecutionEngineServices(
                executionStore,
                contextStore,
                accessor,
                contextFactory,
                serviceProvider,
                pipelineExecutor,
                logger,
                cleanupService,
                Options.Create(aiOptions),
                observability,
                payloadCompactor,
                stateReader,
                stateWriter,
                stepResolver,
                runtimeInstanceIdentity,
                policyFactory,
                concurrencyGate,
                stepExecutionOrchestrator,
                executionControlGate,
                executionControlService,
                replayMetadataService,
                null);

            var runtimeServices = AiDagExecutionEngineRuntimeServicesFixture.Create(engineServices);

            return new AiDagExecutionEngine(engineServices, runtimeServices);
        }

        /// <summary>
        /// Creates a local retry pipeline whose step always fails.
        /// </summary>
        private static AiPipelineDefinition CreateLocalRetryPipeline()
        {
            return new AiPipelineDefinition
            {
                Name = "dag-retry-local",
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "step-1",
                        StepKey = "test.fail",
                        Order = 1,
                        DependsOn = new List<string>(),
                        Config = new Dictionary<string, object?>
                        {
                            ["retry"] = new Dictionary<string, object?>
                            {
                                ["policies"] = new[]
                                {
                                    "retry.transient.default"
                                },
                                ["maxRetries"] = 3,
                                ["strategy"] = "Fixed",
                                ["baseDelayMs"] = 50,
                                ["maxDelayMs"] = 50,
                                ["jitter"] = false
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates a local retry pipeline whose step fails once and then succeeds.
        /// </summary>
        private static AiPipelineDefinition CreateLocalRetrySuccessPipeline()
        {
            return new AiPipelineDefinition
            {
                Name = "dag-retry-local-success",
                Version = "1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = new List<AiPipelineStepDefinition>
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "step-1",
                        StepKey = "test.fail.then.success",
                        Order = 1,
                        DependsOn = new List<string>(),
                        Config = new Dictionary<string, object?>
                        {
                            ["retry"] = new Dictionary<string, object?>
                            {
                                ["policies"] = new[]
                                {
                                    "retry.transient.default"
                                },
                                ["maxRetries"] = 3,
                                ["strategy"] = "Fixed",
                                ["baseDelayMs"] = 0,
                                ["maxDelayMs"] = 0,
                                ["jitter"] = false
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Creates an in-memory pipeline source selector for the supplied pipeline.
        /// </summary>
        private static IAiPipelineDefinitionSourceSelector CreateInMemorySourceSelector(
            AiPipelineDefinition pipeline)
        {
            var services = new ServiceCollection();

            services.AddOptions();

            services.Configure<AiEngineOptions>(options =>
            {
                options.DefaultPipelineDefinitionSource = "InMemory";
            });

            var provider = new InMemoryAiPipelineDefinitionProvider(
                new[] { pipeline });

            services.AddSingleton<IAiPipelineDefinitionProvider>(provider);
            services.AddSingleton(provider);

            var serviceProvider = services.BuildServiceProvider();

            return new DefaultAiPipelineDefinitionSourceSelector(
                serviceProvider.GetRequiredService<IOptions<AiEngineOptions>>(),
                serviceProvider);
        }

        /// <summary>
        /// Creates a deterministic runtime context for local engine tests.
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
        /// Resolves a required service from the engine private service provider.
        /// </summary>
        private static T GetRequiredService<T>(
            AiDagExecutionEngine engine)
        {
            var property = typeof(AiExecutionEngine)
                .GetProperty(
                    "Services",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

            var provider = (IServiceProvider)property!.GetValue(engine)!;

            return (T)(provider.GetService(typeof(T))
                ?? throw new InvalidOperationException(
                    $"Required service '{typeof(T).FullName}' is not registered."));
        }

        /// <summary>
        /// Test step that always fails.
        /// </summary>
        [AiStep("test.fail")]
        private sealed class TestFailStep : IAiStep
        {
            /// <summary>
            /// Gets the step name.
            /// </summary>
            public string Name => "test.fail";

            /// <inheritdoc />
            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiStepResult.Fail("boom"));
            }
        }

        /// <summary>
        /// Test step that fails once and then succeeds.
        /// </summary>
        [AiStep("test.fail.then.success")]
        private sealed class TestFailThenSuccessStep : IAiStep
        {
            private static int _counter;

            /// <summary>
            /// Gets the step name.
            /// </summary>
            public string Name => "test.fail.then.success";

            /// <summary>
            /// Resets the static attempt counter.
            /// </summary>
            public static void Reset()
            {
                _counter = 0;
            }

            /// <inheritdoc />
            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                _counter++;

                if (_counter == 1)
                {
                    return Task.FromResult(AiStepResult.Fail("boom"));
                }

                return Task.FromResult(AiStepResult.Ok("ok"));
            }
        }

        /// <summary>
        /// Payload resolver that should not be used in this test.
        /// </summary>
        private sealed class NoopPayloadResolver : IAiExecutionPayloadResolver
        {
            /// <inheritdoc />
            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(
                    "Payload resolution is not expected in this test.");
            }
        }

        /// <summary>
        /// No-op step result payload compactor.
        /// </summary>
        private sealed class NoopStepResultPayloadCompactor : IAiStepResultPayloadCompactor
        {
            /// <inheritdoc />
            public Task CompactAsync(
                AiStepResult result,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Local step resolver backed only by hot execution state.
        /// </summary>
        private sealed class LocalStateExecutionStepResolver : IAiExecutionStepResolver
        {
            /// <inheritdoc />
            public Task WarmAsync(
                string executionId,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task WarmStepsAsync(
                string executionId,
                AiExecutionState state,
                IReadOnlyCollection<string> stepNames,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiStepState?> GetStepAsync(
                string executionId,
                string stepName,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                state.Steps.TryGetValue(stepName, out var step);

                return Task.FromResult(step);
            }

            /// <inheritdoc />
            public Task<AiStepState?> GetStepStatusAsync(
                string executionId,
                string stepName,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                state.Steps.TryGetValue(stepName, out var step);

                return Task.FromResult(step);
            }
        }

        /// <summary>
        /// Minimal service provider for local engine tests.
        /// </summary>
        private sealed class TestServiceProvider : IServiceProvider
        {
            private readonly Dictionary<Type, object> _services;

            /// <summary>
            /// Initializes a new instance of the <see cref="TestServiceProvider"/> class.
            /// </summary>
            /// <param name="services">The service map.</param>
            public TestServiceProvider(Dictionary<Type, object> services)
            {
                _services = services;
                _services[typeof(IServiceProvider)] = this;
            }

            /// <inheritdoc />
            public object? GetService(Type serviceType)
            {
                return _services.TryGetValue(serviceType, out var service)
                    ? service
                    : null;
            }
        }
    }
}
