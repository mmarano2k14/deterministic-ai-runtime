using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>
    /// Exercises the real native policy engines with unsupported custom declarations.
    /// There is no remote transport here and no Redis claim/lease is performed.
    /// </summary>
    public sealed class AiNativePolicyCheckpointTests
    {
        [Theory]
        [InlineData("guard")]
        [InlineData("")]
        public async Task Concurrency_Custom_Declaration_Cannot_Execute_Native_Policy_Or_Allow(string name)
        {
            var native = new CountingPolicy(AiPolicyKind.Concurrency);
            var step = new NeverExecutedStep();
            var context = CreateStepContext(step, Custom(name));
            var engine = new DefaultAiConcurrencyEngine(
                new DefaultAiPolicyRegistry(new[] { native }), context, new TestObservability());

            await Assert.ThrowsAsync<NotSupportedException>(() => engine.DecideAsync(ConcurrencyContext()));
            Assert.Equal(0, native.Calls);
            Assert.Equal(0, step.Calls);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Native_Concurrency_Allow_And_Block_Keep_Existing_Behavior(bool block)
        {
            var native = new CountingPolicy(AiPolicyKind.Concurrency, block);
            var step = new NeverExecutedStep();
            var context = CreateStepContext(step, new AiConfiguredPolicyDefinition { Name = "guard" });
            var engine = new DefaultAiConcurrencyEngine(
                new DefaultAiPolicyRegistry(new[] { native }), context, new TestObservability());

            var decision = await engine.DecideAsync(ConcurrencyContext());
            Assert.Equal(!block, decision.Allowed);
            Assert.Equal(1, native.Calls);
            Assert.Equal(0, step.Calls);
        }

        [Fact]
        public async Task Delegation_Custom_Declaration_Cannot_Execute_Native_Guard()
        {
            var native = new CountingPolicy(AiPolicyKind.Delegation);
            var step = new NeverExecutedStep();
            var context = CreateStepContext(step, new AiConfiguredPolicyDefinition { Name = "guard" });
            var engine = new DefaultAiChildDelegationPolicyEngine(
                new DefaultAiPolicyRegistry(new[] { native }), context, new TestObservability());
            var relation = ChildDagCompositionTestData.CreateRelation(AiChildExecutionRelationStatus.DelegationPolicyPending);
            var definition = new AiChildDelegationPolicyDefinition { Policies = new List<AiConfiguredPolicyDefinition> { Custom("guard") } };

            await Assert.ThrowsAsync<NotSupportedException>(() => engine.EvaluateAsync(relation, definition));
            Assert.Equal(0, native.Calls);
            Assert.Equal(0, step.Calls);
        }

        private static AiConfiguredPolicyDefinition Custom(string name) => new()
        {
            Name = name, ExecutionLanguage = "python",
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "publication/guard/v1" }
        };

        private static AiConcurrencyContext ConcurrencyContext() => new()
        {
            ExecutionId = "execution-1", PipelineKey = "test:v1", StepId = "work",
            StepKey = "work", RuntimeInstanceId = "runtime-1", LeaseId = "lease-1"
        };

        private static AiStepExecutionContext CreateStepContext(IAiStep step, AiConfiguredPolicyDefinition policy)
        {
            var record = new AiExecutionRecord { ExecutionId = "execution-1", PipelineName = "test" };
            var state = new AiExecutionState { ExecutionId = record.ExecutionId, PipelineName = record.PipelineName };
            var definition = new AiConcurrencyDefinition
            {
                Enabled = true, Policies = new List<AiConfiguredPolicyDefinition> { policy }
            };
            var resolved = new ResolvedAiPipelineStep
            {
                Name = "work", StepKey = "work", Step = step,
                Config = new Dictionary<string, object?> { ["concurrency"] = definition }
            };
            return new AiStepExecutionContext(ChildDagCompositionTestData.CreateExecutionContext(record, state), resolved);
        }

        private sealed class CountingPolicy : IAiPolicy
        {
            private readonly bool block;
            public CountingPolicy(AiPolicyKind kind, bool block = false) { Kind = kind; this.block = block; }
            public string Key => "guard";
            public AiPolicyKind Kind { get; }
            public int Calls { get; private set; }
            public Task<AiPolicyResult> ExecuteAsync(object context, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(block ? AiPolicyResult.Block("native denied") : AiPolicyResult.Success("native allowed"));
            }
        }

        private sealed class NeverExecutedStep : IAiStep
        {
            public string Name => "work";
            public int Calls { get; private set; }
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default)
            {
                Calls++;
                throw new InvalidOperationException("Admission must not execute the step body.");
            }
        }

        private sealed class TestObservability : IAiRuntimeObservability
        {
            public IAiRuntimeMetrics Metrics { get; } = new TestMetrics();
            public IAiRuntimeTracer Tracer { get; } = new NoOpAiRuntimeTracer();
            public IAiDecisionLedgerRecorder Ledger => null!;
            public IAiRuntimeCorrelationAccessor Correlation => null!;
        }

        private sealed class TestMetrics : IAiRuntimeMetrics
        {
            public Multiplexed.Abstractions.AI.Observability.Metrics.Execution.IAiExecutionMetrics Execution => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Retention.IAiRetentionMetrics Retention => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Storage.IAiStorageMetrics Storage => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.HotState.IAiHotStateMetrics HotState => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Resolvers.IAiResolverMetrics Resolver => null!;
            public Multiplexed.Abstractions.AI.Observability.Metrics.Policy.IAiPolicyMetrics Policy { get; } =
                new AiPolicyMetrics(NoOpAiRuntimeMetricWriter.Instance);
            public Multiplexed.Abstractions.AI.Observability.Metrics.Workers.IAiRuntimeInstanceWorkerMetrics Worker => null!;
        }
    }
}
