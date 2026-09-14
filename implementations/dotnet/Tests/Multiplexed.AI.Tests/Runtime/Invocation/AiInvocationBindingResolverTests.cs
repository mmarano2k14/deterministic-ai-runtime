using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.Invocation;
using System.Text.Json;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Metadata-resolution tests only. No Python/Node/.NET worker is executed.</summary>
    public sealed class AiInvocationBindingResolverTests
    {
        private readonly AiInvocationBindingResolver resolver = new();

        [Fact]
        public void Legacy_Native_Step_Has_No_Language()
        {
            var result = resolver.ResolveStep(Pipeline(), Native());
            Assert.Equal(AiInvocationBinding.Native, result);
        }

        [Theory]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public void Custom_Step_Inherits_Pipeline_Default(string language)
        {
            var result = resolver.ResolveStep(Pipeline(language), Custom("work"));
            Assert.Equal(AiInvocationKind.Custom, result.Kind);
            Assert.Equal(language, result.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, result.LanguageSource);
            Assert.Equal("publication/work/v1", result.ImplementationRef);
        }

        [Fact]
        public void Override_Does_Not_Propagate_To_Successor_Or_Mutate_Definition()
        {
            var first = Custom("first", "typescript");
            var next = Custom("next");
            var pipeline = Pipeline("python", first, next);
            var before = JsonSerializer.Serialize(pipeline);

            Assert.Equal("typescript", resolver.ResolveStep(pipeline, first).ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Step, resolver.ResolveStep(pipeline, first).LanguageSource);
            Assert.Equal("python", resolver.ResolveStep(pipeline, next).ExecutionLanguage);
            Assert.Null(next.ExecutionLanguage);
            Assert.Equal(before, JsonSerializer.Serialize(pipeline));
        }

        [Fact]
        public async Task Parallel_Resolutions_Do_Not_Contaminate_Each_Other()
        {
            var step = Custom("same-name");
            var tasks = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
            {
                var language = index % 2 == 0 ? "python" : "typescript";
                var binding = resolver.ResolveStep(Pipeline(language, step), step);
                Assert.Equal(language, binding.ExecutionLanguage);
                return binding;
            }));
            var results = await Task.WhenAll(tasks);
            Assert.Equal(100, results.Length);
            Assert.Null(step.ExecutionLanguage);
        }

        [Fact]
        public void Custom_Step_Without_Effective_Language_Is_Invalid()
        {
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline(), Custom("work")));
        }

        [Fact]
        public void Custom_Override_Works_Without_Pipeline_Default()
        {
            var binding = resolver.ResolveStep(Pipeline(), Custom("work", "python"));
            Assert.Equal("python", binding.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Step, binding.LanguageSource);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("Python")]
        [InlineData("python ")]
        [InlineData("mcp")]
        [InlineData("rust")]
        public void Explicit_Invalid_Local_Language_Does_Not_Fall_Back(string language)
        {
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolveStep(Pipeline("python"), Custom("work", language)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("mcp")]
        [InlineData("unknown")]
        public void Invalid_Pipeline_Default_Is_Rejected_Even_For_Native_Step(string language)
        {
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline(language), Native()));
        }

        [Fact]
        public void Native_Step_Does_Not_Inherit_Custom_Language()
        {
            Assert.Equal(AiInvocationBinding.Native, resolver.ResolveStep(Pipeline("python"), Native()));
        }

        [Fact]
        public void Mcp_Step_Is_Not_A_Python_Worker()
        {
            var binding = resolver.ResolveStep(Pipeline("python"), Mcp());
            Assert.Equal(AiInvocationKind.Mcp, binding.Kind);
            Assert.Null(binding.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.None, binding.LanguageSource);
            Assert.Equal("reports", binding.ConnectionRef);
            Assert.Equal("publish", binding.Tool);
        }

        [Theory]
        [InlineData(AiInvocationKind.Native)]
        [InlineData(AiInvocationKind.Mcp)]
        public void NonCustom_Step_Cannot_Declare_Local_Language(AiInvocationKind kind)
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", StepKey = "same-key", ExecutionLanguage = "python",
                Invocation = kind == AiInvocationKind.Native ? null : Mcp().Invocation
            };
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"), step));
        }

        [Fact]
        public void Empty_Descriptor_Is_Not_Implicitly_Native()
        {
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"),
                new AiPipelineStepDefinition { Name = "work", Invocation = new AiInvocationDefinition() }));
        }

        [Fact]
        public void Undefined_Invocation_Kind_Is_Rejected()
        {
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"),
                new AiPipelineStepDefinition
                {
                    Name = "work", Invocation = new AiInvocationDefinition { Kind = (AiInvocationKind)999 }
                }));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void Custom_Requires_Implementation_Reference(string? reference)
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Custom, ImplementationRef = reference
                }
            };
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"), step));
        }

        [Fact]
        public void Mixed_Invocation_Fields_Are_Rejected()
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Custom, ImplementationRef = "publication/work/v1", Tool = "publish"
                }
            };
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"), step));
        }

        [Fact]
        public void Native_Cannot_Hide_An_Implementation_Reference()
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Native, ImplementationRef = "publication/work/v1"
                }
            };
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"), step));
        }

        [Theory]
        [InlineData(null, "publish")]
        [InlineData("", "publish")]
        [InlineData("reports", null)]
        [InlineData("reports", " ")]
        public void Mcp_Requires_Connection_And_Tool(string? connection, string? tool)
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Mcp, ConnectionRef = connection, Tool = tool
                }
            };
            Assert.Throws<InvalidOperationException>(() => resolver.ResolveStep(Pipeline("python"), step));
        }

        [Fact]
        public void Step_Policy_Inherits_Declaring_Custom_Step_Override()
        {
            var step = Custom("format", "typescript");
            var binding = resolver.ResolvePolicy(Pipeline("python", step), Policy(), AiPolicyBindingScope.Step, step);
            Assert.Equal("typescript", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Step, binding.Invocation.LanguageSource);
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal("format", binding.OwnerStepName);
        }

        [Fact]
        public void Policy_Override_Wins_Over_Step_And_Pipeline()
        {
            var step = Custom("format", "typescript");
            var binding = resolver.ResolvePolicy(Pipeline("python", step), Policy("dotnet"), AiPolicyBindingScope.Step, step);
            Assert.Equal("dotnet", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Policy, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Pipeline_Policy_Does_Not_Inherit_Evaluation_Step_Override()
        {
            var step = Custom("format", "typescript");
            var pipeline = Pipeline("python", step);
            var local = resolver.ResolvePolicy(pipeline, Policy(), AiPolicyBindingScope.Step, step);
            var global = resolver.ResolvePolicy(pipeline, Policy(), AiPolicyBindingScope.Pipeline);
            Assert.Equal("typescript", local.Invocation.ExecutionLanguage);
            Assert.Equal("python", global.Invocation.ExecutionLanguage);
            Assert.Equal(AiPolicyBindingScope.Pipeline, global.Scope);
            Assert.Null(global.OwnerStepName);
        }

        [Theory]
        [InlineData(AiInvocationKind.Native)]
        [InlineData(AiInvocationKind.Mcp)]
        public void Custom_Policy_On_NonCustom_Step_Uses_Pipeline_Default(AiInvocationKind kind)
        {
            var step = kind == AiInvocationKind.Native ? Native() : Mcp();
            var binding = resolver.ResolvePolicy(Pipeline("python", step), Policy(), AiPolicyBindingScope.Step, step);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Native_Policy_Remains_Native_In_Custom_Pipeline()
        {
            var policy = new AiConfiguredPolicyDefinition { Name = "governance", Kind = "Concurrency" };
            var binding = resolver.ResolvePolicy(Pipeline("python"), policy, AiPolicyBindingScope.Pipeline);
            Assert.Equal(AiInvocationBinding.Native, binding.Invocation);
        }

        [Fact]
        public void Mcp_Policy_Is_Rejected()
        {
            var policy = new AiConfiguredPolicyDefinition { Name = "guard", Invocation = Mcp().Invocation };
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline("python"), policy, AiPolicyBindingScope.Pipeline));
        }

        [Fact]
        public void Policy_Requires_Original_Scope_Not_An_Ambient_Step()
        {
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline("python"), Policy(), AiPolicyBindingScope.Step));
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline("python"), Policy(), AiPolicyBindingScope.Pipeline, Native()));
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline("python"), Policy(), (AiPolicyBindingScope)999));
        }

        [Fact]
        public void Policy_Binding_Is_Detached_From_Mutable_Declaration()
        {
            var policy = Policy("python");
            var binding = resolver.ResolvePolicy(Pipeline(), policy, AiPolicyBindingScope.Pipeline);
            policy.Name = "other";
            policy.ExecutionLanguage = "typescript";
            policy.Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Native };
            Assert.Equal("guard", binding.PolicyName);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
            Assert.Equal("publication/guard/v1", binding.Invocation.ImplementationRef);
        }

        [Fact]
        public void Custom_Policy_Without_Effective_Language_Is_Rejected()
        {
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline(), Policy(), AiPolicyBindingScope.Pipeline));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("unknown")]
        public void Invalid_Policy_Override_Does_Not_Fall_Back(string language)
        {
            Assert.Throws<InvalidOperationException>(() =>
                resolver.ResolvePolicy(Pipeline("python"), Policy(language), AiPolicyBindingScope.Pipeline));
        }

        private static AiPipelineDefinition Pipeline(string? language = null, params AiPipelineStepDefinition[] steps) => new()
        {
            Name = "test", ExecutionLanguage = language, Steps = steps
        };

        private static AiPipelineStepDefinition Native() => new() { Name = "native", StepKey = "same-key" };

        private static AiPipelineStepDefinition Custom(string name, string? language = null) => new()
        {
            Name = name, StepKey = "same-key", ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = $"publication/{name}/v1" }
        };

        private static AiPipelineStepDefinition Mcp() => new()
        {
            Name = "publish", StepKey = "same-key",
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Mcp, ConnectionRef = "reports", Tool = "publish" }
        };

        private static AiConfiguredPolicyDefinition Policy(string? language = null) => new()
        {
            Name = "guard", Kind = "Concurrency", ExecutionLanguage = language,
            Invocation = new AiInvocationDefinition { Kind = AiInvocationKind.Custom, ImplementationRef = "publication/guard/v1" }
        };
    }
}
