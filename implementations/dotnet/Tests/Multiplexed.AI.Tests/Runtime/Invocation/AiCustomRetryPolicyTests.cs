using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.Invocation;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiCustomRetryPolicyTests
    {
        [Fact]
        public void Pipeline_Retry_Custom_Binding_Uses_Pipeline_Scope()
        {
            var pipeline = Pipeline("python", PipelineRetry(Custom("retry.remote")));
            var binding = Assert.Single(new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
            Assert.Equal(AiPolicyBindingScope.Pipeline, binding.Scope);
            Assert.Null(binding.OwnerStepName);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Step_Retry_Replaces_Pipeline_Retry_For_That_Step()
        {
            var step = Step(StepRetry(Custom("retry.step", "typescript")));
            var pipeline = Pipeline("python", PipelineRetry(Custom("retry.pipeline")), step);
            var binding = Assert.Single(new AiRetryPolicyBindingResolver().Resolve(pipeline, step));
            Assert.Equal("retry.step", binding.PolicyName);
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal("typescript", binding.Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Custom_Retry_May_Inherit_Custom_Step_Language()
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work",
                StepKey = "native",
                Config = StepRetry(Custom("retry.step")),
                ExecutionLanguage = "dotnet",
                Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "impl-step" }
            };
            var pipeline = Pipeline("python", null, step);
            var binding = Assert.Single(new AiRetryPolicyBindingResolver().Resolve(pipeline, step));
            Assert.Equal("dotnet", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Step, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Native_Retry_Remains_Native()
        {
            var pipeline = Pipeline("python", PipelineRetry(new AiConfiguredPolicyDefinition { Name = "retry.transient.default" }));
            var binding = Assert.Single(new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
            Assert.Equal(AiInvocationKind.Native, binding.Invocation.Kind);
        }

        [Fact]
        public void Custom_Retry_Without_Effective_Language_Is_Refused()
        {
            var pipeline = Pipeline(null, PipelineRetry(Custom("retry.remote")));
            Assert.Throws<InvalidOperationException>(() => new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
        }

        [Fact]
        public void Blank_Native_Retry_Entry_Is_Skipped()
        {
            var pipeline = Pipeline("python", PipelineRetry(new AiConfiguredPolicyDefinition { Name = "" }));
            Assert.Empty(new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
        }

        [Fact]
        public void Blank_Custom_Retry_Entry_Is_Refused()
        {
            var policy = Custom(""); var pipeline = Pipeline("python", PipelineRetry(policy));
            Assert.Throws<NotSupportedException>(() => new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
        }

        [Fact]
        public void Policy_Local_Language_Overrides_Pipeline_Default()
        {
            var pipeline = Pipeline("python", PipelineRetry(Custom("retry.remote", "typescript")));
            var binding = Assert.Single(new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single()));
            Assert.Equal("typescript", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Policy, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Native_And_Custom_Retry_Bindings_Preserve_Declaration_Order()
        {
            var pipeline = Pipeline("python", PipelineRetry(
                new AiConfiguredPolicyDefinition { Name = "retry.transient.default" },
                Custom("retry.remote")));
            var bindings = new AiRetryPolicyBindingResolver().Resolve(pipeline, pipeline.Steps.Single());
            Assert.Equal(2, bindings.Count);
            Assert.Equal("retry.transient.default", bindings[0].PolicyName);
            Assert.Equal(AiInvocationKind.Native, bindings[0].Invocation.Kind);
            Assert.Equal("retry.remote", bindings[1].PolicyName);
            Assert.Equal(AiInvocationKind.Custom, bindings[1].Invocation.Kind);
        }

        [Fact]
        public void Retry_Capability_Is_Hosted()
        {
            var capability = AiCustomPolicyFamilyCapabilities.Get(Multiplexed.AI.Abstractions.AI.Policies.AiPolicyKind.Retry);
            Assert.True(capability.SupportsHostedExecution);
            Assert.Equal(AiCustomPolicyFamilyContracts.RetryV1, capability.ContractId);
        }

        private static AiConfiguredPolicyDefinition Custom(string name, string? language = null) => new()
        {
            Name = name, Kind = "Retry", ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "impl-" + (name.Length == 0 ? "blank" : name) }
        };
        private static Dictionary<string, object?> PipelineRetry(params AiConfiguredPolicyDefinition[] policies) => RetryConfig(policies);
        private static Dictionary<string, object?> StepRetry(params AiConfiguredPolicyDefinition[] policies) => RetryConfig(policies);
        private static Dictionary<string, object?> RetryConfig(IEnumerable<AiConfiguredPolicyDefinition> policies) => new()
        {
            ["retry"] = new AiRetryPolicyDefinition { Policies = policies.ToList(), MaxRetries = 3 }
        };
        private static AiPipelineStepDefinition Step(Dictionary<string, object?>? config = null) => new()
        { Name = "work", StepKey = "native", Config = config ?? new Dictionary<string, object?>() };
        private static AiPipelineDefinition Pipeline(string? language, Dictionary<string, object?>? config, AiPipelineStepDefinition? step = null) => new()
        {
            Name = "p", Version = "1", ExecutionMode = Multiplexed.Abstractions.AI.Execution.AiExecutionMode.Dag,
            ExecutionLanguage = language, Config = config ?? new Dictionary<string, object?>(), Steps = new[] { step ?? Step() }
        };
    }
}
