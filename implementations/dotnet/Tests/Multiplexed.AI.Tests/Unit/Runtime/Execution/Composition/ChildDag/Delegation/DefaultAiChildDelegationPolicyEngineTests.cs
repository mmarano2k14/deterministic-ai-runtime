using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Relations;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Delegation
{
    /// <summary>
    /// Validates child delegation policy resolution and execution through the existing policy engine infrastructure.
    /// </summary>
    public sealed class DefaultAiChildDelegationPolicyEngineTests
    {
        [Fact]
        public void PolicyEngineFactory_Should_Create_Delegation_Engine_For_Delegation_Kind()
        {
            var stepContext = CreateStepContext(null, null);
            var policyRegistry = new DefaultAiPolicyRegistry(Array.Empty<IAiPolicy>());
            var engineRegistry = new DefaultAiPolicyEngineRegistry(
                new[] { typeof(DefaultAiChildDelegationPolicyEngine) });
            var factory = new DefaultAiPolicyEngineFactory(
                policyRegistry,
                engineRegistry,
                new TestRuntimeObservability());

            var engine = factory.Create<IAiChildDelegationPolicyEngine>(
                AiPolicyKind.Delegation,
                stepContext);

            Assert.IsType<DefaultAiChildDelegationPolicyEngine>(engine);
        }

        [Fact]
        public async Task ResolveDefinitionAsync_Should_Prefer_Step_Override_Over_Pipeline_Fallback()
        {
            var stepDefinition = CreatePolicyDefinition("delegation.step");
            var pipelineDefinition = CreatePolicyDefinition("delegation.pipeline");
            var stepContext = CreateStepContext(stepDefinition, pipelineDefinition);
            var engine = CreateEngine(stepContext, Array.Empty<IAiPolicy>());

            var resolved = await engine.ResolveDefinitionAsync();

            Assert.Equal("delegation.step", Assert.Single(resolved.Policies).Name);
        }

        [Fact]
        public async Task ResolveDefinitionAsync_Should_Use_Pipeline_Fallback_When_Step_Override_Is_Missing()
        {
            var pipelineDefinition = CreatePolicyDefinition("delegation.pipeline");
            var stepContext = CreateStepContext(null, pipelineDefinition);
            var engine = CreateEngine(stepContext, Array.Empty<IAiPolicy>());

            var resolved = await engine.ResolveDefinitionAsync();

            Assert.Equal("delegation.pipeline", Assert.Single(resolved.Policies).Name);
        }

        [Fact]
        public async Task ResolveDefinitionAsync_Should_Default_To_Allow_When_No_Delegation_Policies_Are_Configured()
        {
            var stepContext = CreateStepContext(null, null);
            var engine = CreateEngine(stepContext, Array.Empty<IAiPolicy>());

            var resolved = await engine.ResolveDefinitionAsync();

            Assert.Empty(resolved.Policies);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Execute_Registered_Delegation_Policy_With_Config()
        {
            var stepContext = CreateStepContext(null, null);
            var engine = CreateEngine(
                stepContext,
                new IAiPolicy[] { new ConfiguredDelegationPolicy() });
            var relation = CreateRelation();
            var definition = new AiChildDelegationPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = ConfiguredDelegationPolicy.PolicyKey,
                        Config = new Dictionary<string, object?>
                        {
                            ["allow"] = true
                        }
                    }
                ]
            };

            var results = await engine.EvaluateAsync(relation, definition);

            var result = Assert.Single(results);
            Assert.True(result.IsSuccess);
            Assert.Equal("configured delegation allowed", result.Message);
        }

        private static DefaultAiChildDelegationPolicyEngine CreateEngine(
            AiStepExecutionContext stepContext,
            IEnumerable<IAiPolicy> policies)
        {
            return new DefaultAiChildDelegationPolicyEngine(
                new DefaultAiPolicyRegistry(policies),
                stepContext,
                new TestRuntimeObservability());
        }

        private static AiChildDelegationPolicyDefinition CreatePolicyDefinition(string policyName)
        {
            return new AiChildDelegationPolicyDefinition
            {
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = policyName
                    }
                ]
            };
        }

        private static AiStepExecutionContext CreateStepContext(
            AiChildDelegationPolicyDefinition? stepDefinition,
            AiChildDelegationPolicyDefinition? pipelineDefinition)
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = "parent-execution-1",
                PipelineName = "parent-pipeline"
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName
            };

            if (pipelineDefinition is not null)
            {
                state.PipelineConfig[AiChildDelegationPolicyDefinition.ConfigKey] = pipelineDefinition;
            }

            var resolvedStep = new ResolvedAiPipelineStep
            {
                Name = "delegate-child",
                StepKey = "delegate-child",
                Step = new NoOpStep(),
                Config = stepDefinition is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?>
                    {
                        [AiChildDelegationPolicyDefinition.ConfigKey] = stepDefinition
                    }
            };

            var executionContext = ChildDagCompositionTestData.CreateExecutionContext(record, state);

            return new AiStepExecutionContext(executionContext, resolvedStep);
        }

        private static AiChildExecutionRelation CreateRelation()
        {
            return new AiChildExecutionRelation
            {
                TenantId = "tenant-1",
                ControlPlaneId = "control-plane-policy-tests",
                ParentExecutionId = "parent-execution-1",
                ParentCallSiteId = "delegate-child",
                ChildDagId = "child-analysis",
                ChildDagDefinitionVersion = "v1",
                FrozenChildDagDefinition = AiStoredPayload.Inline("{}", contentHash: "definition"),
                CanonicalLogicalInvocationKey = "portfolio-42|MSFT",
                ChildInvocationKey = "child-invocation-test",
                InvocationGeneration = 0,
                FrozenInvocationInput = AiStoredPayload.Inline("{}", contentHash: "input"),
                DelegationPolicyBindingSnapshot = AiStoredPayload.Inline("{\"Policies\":[]}", contentHash: "binding"),
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-14T00:00:00Z")
            };
        }

        [AiPolicy(PolicyKey, Kind = AiPolicyKind.Delegation)]
        private sealed class ConfiguredDelegationPolicy : AiPolicyBase<AiChildDelegationPolicyContext>
        {
            public const string PolicyKey = "delegation.configured-test";

            public override string Key => PolicyKey;

            public override AiPolicyKind Kind => AiPolicyKind.Delegation;

            public override Task<AiPolicyResult> ExecuteAsync(
                AiChildDelegationPolicyContext context,
                CancellationToken cancellationToken = default)
            {
                var allowed = context.Config.TryGetValue("allow", out var value) &&
                              value is bool boolean &&
                              boolean;

                return Task.FromResult(
                    allowed
                        ? AiPolicyResult.Success("configured delegation allowed")
                        : AiPolicyResult.Block("configured delegation denied"));
            }
        }

        private sealed class NoOpStep : IAiStep
        {
            public string Name => "delegate-child";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiStepResult.Ok());
            }
        }


        private sealed class TestRuntimeObservability : IAiRuntimeObservability
        {
            public IAiRuntimeMetrics Metrics { get; } = new TestRuntimeMetrics();

            public IAiRuntimeTracer Tracer { get; } = new NoOpAiRuntimeTracer();

            public IAiDecisionLedgerRecorder Ledger => null!;

            public IAiRuntimeCorrelationAccessor Correlation => null!;
        }

        private sealed class TestRuntimeMetrics : IAiRuntimeMetrics
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
