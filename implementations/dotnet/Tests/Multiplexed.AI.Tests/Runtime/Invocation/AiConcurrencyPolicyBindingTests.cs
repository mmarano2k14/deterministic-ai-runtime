using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;
using System.Text.Json;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiConcurrencyPolicyBindingTests
    {
        [Fact]
        public void Pipeline_Policy_Keeps_Pipeline_Language_On_An_Overridden_Step()
        {
            var step = Custom(language: "typescript");
            var pipeline = CreatePipeline(new[] { step }, config: Config(CustomPolicy()));
            var binding = Assert.Single(new AiConcurrencyPolicyBindingResolver().Resolve(pipeline, step));
            Assert.Equal(AiPolicyBindingScope.Pipeline, binding.Scope);
            Assert.Null(binding.OwnerStepName);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, binding.Invocation.LanguageSource);
        }

        [Theory]
        [InlineData(null, "typescript", AiExecutionLanguageSource.Step)]
        [InlineData("dotnet", "dotnet", AiExecutionLanguageSource.Policy)]
        public void Step_Policy_Uses_Local_Language_Unless_Policy_Overrides(string? policyLanguage, string expected, AiExecutionLanguageSource source)
        {
            var step = Custom(language: "typescript", config: Config(CustomPolicy(language: policyLanguage)));
            var pipeline = CreatePipeline(new[] { step }, config: Config(CustomPolicy("ignored")));
            var binding = Assert.Single(new AiConcurrencyPolicyBindingResolver().Resolve(pipeline, step));
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal("work", binding.OwnerStepName);
            Assert.Equal(expected, binding.Invocation.ExecutionLanguage);
            Assert.Equal(source, binding.Invocation.LanguageSource);
            Assert.Equal("guard", binding.PolicyName);
            Assert.Equal(new DefaultAiConcurrencyDefinitionResolver().Resolve(pipeline, step).Policies.Select(p => p.Name), new[] { binding.PolicyName });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Native_And_Mcp_Owners_Do_Not_Supply_A_Custom_Language(bool mcp)
        {
            var config = Config(CustomPolicy());
            var step = mcp ? Mcp(config: config) : Native(config: config);
            var binding = Assert.Single(new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step));
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal("python", binding.Invocation.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Empty_Local_Policy_List_Inherits_The_Pipeline_Scope()
        {
            var step = Custom(language: "typescript", config: Config());
            var pipeline = CreatePipeline(new[] { step }, config: Config(CustomPolicy()));
            var result = new AiConcurrencyPolicyBindingResolver().Resolve(pipeline, step);
            Assert.Equal(AiPolicyBindingScope.Pipeline, Assert.Single(result).Scope);
            Assert.Equal("python", result[0].Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Native_Guard_Remains_Native_And_Ordered_Duplicate_Names_Are_Not_Collapsed()
        {
            var step = Custom(config: Config(
                new AiConfiguredPolicyDefinition { Name = "guard" }, CustomPolicy(), CustomPolicy("last")));
            var bindings = new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step);
            Assert.Equal(new[] { "guard", "guard", "last" }, bindings.Select(x => x.PolicyName));
            Assert.Equal(AiInvocationBinding.Native, bindings[0].Invocation);
            Assert.Null(bindings[0].Invocation.ExecutionLanguage);
            Assert.Equal(AiInvocationKind.Custom, bindings[1].Invocation.Kind);
        }

        [Fact]
        public void Scope_Remains_Step_When_Language_Comes_From_Pipeline()
        {
            var step = Custom(config: Config(CustomPolicy()));
            var binding = Assert.Single(new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step));
            Assert.Equal(AiPolicyBindingScope.Step, binding.Scope);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, binding.Invocation.LanguageSource);
        }

        [Fact]
        public void Metadata_Read_Does_Not_Append_Throttle_Rules_Or_Alter_Policy_Config()
        {
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "concurrency.throttle", Config = new Dictionary<string, object?> { ["scope"] = "provider", ["limit"] = 2 }
            };
            var concurrency = new AiConcurrencyDefinition { Enabled = true, Policies = new List<AiConfiguredPolicyDefinition> { policy } };
            var config = new Dictionary<string, object?> { ["concurrency"] = concurrency };
            var step = Native();
            var pipeline = CreatePipeline(new[] { step }, config: config);
            var resolver = new AiConcurrencyPolicyBindingResolver();
            for (var i = 0; i < 3; i++) Assert.Single(resolver.Resolve(pipeline, step));
            Assert.Empty(concurrency.ThrottleRules);
            Assert.Single(concurrency.Policies);
            Assert.Equal(2, policy.Config["limit"]);
            Assert.Equal(2, policy.Config.Count);
        }

        [Fact]
        public void Compiled_Binding_List_And_Metadata_Cannot_Be_Changed_Through_Source_Policies()
        {
            var policy = CustomPolicy();
            var step = Custom(config: Config(policy));
            var bindings = new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step);
            policy.Name = "changed";
            policy.ExecutionLanguage = "dotnet";
            Assert.Equal("guard", bindings[0].PolicyName);
            Assert.Equal("python", bindings[0].Invocation.ExecutionLanguage);
            Assert.Throws<NotSupportedException>(() => ((IList<AiPolicyInvocationBinding>)bindings).Clear());
        }

        [Fact]
        public void Legacy_Native_Blank_Entries_Are_Skipped_But_Custom_Blanks_Are_Refused()
        {
            // Legacy blank entries are tolerated only on the direct CLR configuration path.
            // The dictionary helper takes the JSON path, where an empty name is invalid.
            var step = Native(config: TypedConcurrencyConfig(new AiConfiguredPolicyDefinition { Name = "" }));
            Assert.Empty(new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step));
            step = Native(config: TypedConcurrencyConfig(CustomPolicy("")));
            Assert.Throws<NotSupportedException>(() => new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step));
        }

        [Theory]
        [InlineData(false, false, "")]
        [InlineData(false, false, " ")]
        [InlineData(false, true, "")]
        [InlineData(false, true, " ")]
        [InlineData(true, false, "")]
        [InlineData(true, false, " ")]
        [InlineData(true, true, "")]
        [InlineData(true, true, " ")]
        public void Dictionary_And_Json_Blank_Policies_Are_Rejected_Before_Binding(
            bool custom,
            bool useJsonElement,
            string name)
        {
            var policy = custom
                ? CustomPolicy(name)
                : new AiConfiguredPolicyDefinition { Name = name };
            var config = Config(policy);
            if (useJsonElement)
            {
                config["concurrency"] = JsonSerializer.SerializeToElement(config["concurrency"]);
            }

            var step = Native(config: config);
            var pipeline = CreatePipeline(new[] { step });
            var definitionResolver = new DefaultAiConcurrencyDefinitionResolver();

            // Metadata compilation must not bypass the admission parser's name validation.
            Assert.Throws<JsonException>(() => definitionResolver.ReadPolicyDeclarations(step.Config));
            Assert.Throws<JsonException>(() => definitionResolver.Resolve(pipeline, step));
            Assert.Throws<JsonException>(() => new AiConcurrencyPolicyBindingResolver().Resolve(pipeline, step));
        }

        [Fact]
        public void Json_Aliases_And_Order_Survive_Scope_Compilation()
        {
            const string json = """
            {"name":"test","executionLanguage":"python",
             "config":{"concurrency":{"enabled":true,"policies":["native.first",{"key":"guard","type":"scope","invocation":{"kind":"Custom","implementationRef":"publication/guard/v1"}}]}},
             "steps":[{"name":"work","stepKey":"same-key","executionLanguage":"typescript","invocation":{"kind":"Custom","implementationRef":"publication/work/v1"}}]}
            """;
            var pipeline = JsonSerializer.Deserialize<AiPipelineDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var bindings = new AiConcurrencyPolicyBindingResolver().Resolve(pipeline, Assert.Single(pipeline.Steps));
            Assert.Equal(new[] { "native.first", "guard" }, bindings.Select(x => x.PolicyName));
            Assert.All(bindings, b => Assert.Equal(AiPolicyBindingScope.Pipeline, b.Scope));
            Assert.Equal("python", bindings[1].Invocation.ExecutionLanguage);
        }

        [Fact]
        public void Other_Policy_Families_Are_Not_Reinterpreted_As_Concurrency()
        {
            var step = Native(config: new Dictionary<string, object?> { ["validation"] = Config(CustomPolicy()) });
            Assert.Empty(new AiConcurrencyPolicyBindingResolver().Resolve(CreatePipeline(new[] { step }), step));
        }

        /// <summary>
        /// Builds the direct CLR concurrency representation without a JSON round-trip.
        /// This helper is intentionally local; the shared dictionary helper remains unchanged.
        /// </summary>
        private static Dictionary<string, object?> TypedConcurrencyConfig(
            params AiConfiguredPolicyDefinition[] policies)
        {
            return new Dictionary<string, object?>
            {
                ["concurrency"] = new AiConcurrencyDefinition
                {
                    Enabled = true,
                    Policies = policies.ToList()
                }
            };
        }
    }
}
