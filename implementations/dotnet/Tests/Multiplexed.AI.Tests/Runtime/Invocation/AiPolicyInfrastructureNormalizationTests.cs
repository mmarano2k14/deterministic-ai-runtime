using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.AI.Runtime.Invocation;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiPolicyInfrastructureNormalizationTests
    {
        [Fact]
        public void Retry_Declaration_Reader_Preserves_Case_Insensitive_Key_Support()
        {
            var pipeline = Pipeline(new Dictionary<string, object?>
            {
                ["ReTrY"] = new { maxRetries = 3, policies = Array.Empty<object>() }
            });

            Assert.Empty(new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
        }

        [Fact]
        public void Retry_Declaration_Reader_Still_Rejects_Case_Collisions()
        {
            var pipeline = Pipeline(new Dictionary<string, object?>
            {
                ["retry"] = new { maxRetries = 3, policies = Array.Empty<object>() },
                ["RETRY"] = new { maxRetries = 3, policies = Array.Empty<object>() }
            });

            var error = Assert.Throws<InvalidOperationException>(() =>
                new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));

            Assert.Contains("Ambiguous 'retry'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Delegation_Declaration_Reader_Preserves_Case_Insensitive_Key_Support()
        {
            var step = ChildStep(new Dictionary<string, object?>
            {
                [AiChildDelegationPolicyDefinition.ConfigKey.ToUpperInvariant()] = new { policies = Array.Empty<object>() }
            });
            var pipeline = Pipeline(new Dictionary<string, object?>(), step);

            Assert.Empty(new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));
        }

        [Fact]
        public void Delegation_Declaration_Reader_Still_Rejects_Case_Collisions()
        {
            var key = AiChildDelegationPolicyDefinition.ConfigKey;
            var step = ChildStep(new Dictionary<string, object?>
            {
                [key] = new { policies = Array.Empty<object>() },
                [key.ToUpperInvariant()] = new { policies = Array.Empty<object>() }
            });
            var pipeline = Pipeline(new Dictionary<string, object?>(), step);

            var error = Assert.Throws<InvalidOperationException>(() =>
                new AiDelegationPolicyBindingResolver().Resolve(pipeline, step));

            Assert.Contains("Ambiguous 'delegation'", error.Message, StringComparison.Ordinal);
        }

        private static AiPipelineDefinition Pipeline(
            Dictionary<string, object?> config,
            AiPipelineStepDefinition? step = null) => new()
        {
            Name = "policy-infrastructure",
            Version = "1",
            ExecutionMode = AiExecutionMode.Dag,
            ExecutionLanguage = "python",
            Config = config,
            Steps = new[] { step ?? new AiPipelineStepDefinition { Name = "work", StepKey = "native" } }
        };

        private static AiPipelineStepDefinition ChildStep(Dictionary<string, object?> config) => new()
        {
            Name = "child-call",
            StepKey = ExecuteChildDagStep.StepKey,
            Config = config
        };
    }
}
