using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Delegation;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiInvocationSerializationTests
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        [Fact]
        public void Historical_Pipeline_Json_Shape_And_Property_Order_Are_Unchanged()
        {
            // Expected pre-ML1-A property contract; null additions must stay omitted.
            const string expected = """
            {"Name":"legacy","Version":null,"ExecutionMode":"Sequential","Steps":[{"Name":"work","StepKey":"same-key","Order":0,"DependsOn":[],"Input":{},"Config":{},"Execution":null,"ResolvedMaxRetries":0,"ResolvedRetryDelayMs":0,"HasDependencies":false,"HasRetryPolicy":false}],"Config":{}}
            """;
            var definition = new AiPipelineDefinition
            {
                Name = "legacy", Steps = new[] { new AiPipelineStepDefinition { Name = "work", StepKey = "same-key" } }
            };
            var json = JsonSerializer.Serialize(definition, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.Never });
            Assert.Equal(expected, json);
        }

        [Fact]
        public void New_Step_Fields_RoundTrip_Without_Creating_Execution_Block()
        {
            const string json = """
            {
              "name":"analysis","executionMode":"Dag","executionLanguage":"python",
              "steps":[
                {"name":"format","stepKey":"logical.format","executionLanguage":"typescript",
                 "invocation":{"kind":"Custom","implementationRef":"publication/format/v1"}}
              ]
            }
            """;
            var first = JsonSerializer.Deserialize<AiPipelineDefinition>(json, Options)!;
            var second = JsonSerializer.Deserialize<AiPipelineDefinition>(JsonSerializer.Serialize(first, Options), Options)!;
            var step = Assert.Single(second.Steps);
            Assert.Equal("python", second.ExecutionLanguage);
            Assert.Equal("typescript", step.ExecutionLanguage);
            Assert.Equal(AiInvocationKind.Custom, step.Invocation!.Kind);
            Assert.Equal("publication/format/v1", step.Invocation.ImplementationRef);
            Assert.Null(step.Execution);
            Assert.Equal("typescript", new AiInvocationBindingResolver().ResolveStep(second, step).ExecutionLanguage);
        }

        [Fact]
        public void Legacy_Policy_String_Retains_Canonical_Output()
        {
            var policy = JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>("\"retry.default\"")!;
            Assert.Equal("{\"name\":\"retry.default\",\"config\":{}}", JsonSerializer.Serialize(policy));
            Assert.Null(policy.ExecutionLanguage);
            Assert.Null(policy.Invocation);
        }

        [Fact]
        public void Historical_Aliases_And_New_Policy_Fields_RoundTrip_Together()
        {
            const string json = """
            {"key":"guard","type":"scope","executionLanguage":"dotnet",
             "invocation":{"kind":"Custom","implementationRef":"publication/guard/v1"},
             "config":{"limit":3}}
            """;
            var first = JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>(json)!;
            var written = JsonSerializer.Serialize(first);
            var second = JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>(written)!;
            Assert.Equal("guard", second.Name);
            Assert.Equal("scope", second.Kind);
            Assert.Equal("dotnet", second.ExecutionLanguage);
            Assert.Equal("publication/guard/v1", second.Invocation!.ImplementationRef);
            Assert.Equal(3, ((JsonElement)second.Config["limit"]!).GetInt32());
            using var document = JsonDocument.Parse(written);
            Assert.False(document.RootElement.TryGetProperty("key", out _));
            Assert.False(document.RootElement.TryGetProperty("type", out _));
        }

        [Fact]
        public void Explicit_Empty_Policy_Language_Is_Preserved_Then_Rejected()
        {
            const string json = """
            {"name":"guard","executionLanguage":"",
             "invocation":{"kind":"Custom","implementationRef":"publication/guard/v1"}}
            """;
            var policy = JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>(json)!;
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(policy));
            Assert.Equal("", document.RootElement.GetProperty("executionLanguage").GetString());
            Assert.Throws<InvalidOperationException>(() => new AiInvocationBindingResolver().ResolvePolicy(
                new AiPipelineDefinition { Name = "test", ExecutionLanguage = "python" }, policy, AiPolicyBindingScope.Pipeline));
        }

        [Theory]
        [InlineData("17")]
        [InlineData("true")]
        [InlineData("{}")]
        [InlineData("[]")]
        public void Invalid_Policy_Language_Json_Type_Is_Rejected(string value)
        {
            var json = "{\"name\":\"guard\",\"executionLanguage\":" + value + "}";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>(json));
        }

        [Fact]
        public void Unknown_Invocation_Kind_Is_Not_Ignored_By_Json()
        {
            const string json = """{"name":"guard","invocation":{"kind":"unsupported"}}""";
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AiConfiguredPolicyDefinition>(json));
        }

        [Fact]
        public void Concurrency_Merge_Preserves_Descriptors_And_Existing_List_Precedence()
        {
            const string json = """
            {
              "name":"test","executionLanguage":"python",
              "config":{"concurrency":{"enabled":true,"policies":[
                {"name":"pipeline.guard","executionLanguage":"dotnet",
                 "invocation":{"kind":"Custom","implementationRef":"publication/pipeline.guard/v1"}}
              ]}},
              "steps":[{"name":"work","stepKey":"same-key",
                "config":{"concurrency":{"policies":[
                  {"name":"step.guard","executionLanguage":"typescript",
                   "invocation":{"kind":"Custom","implementationRef":"publication/step.guard/v1"},"config":{"limit":2}}
                ]}}}]
            }
            """;
            var pipeline = JsonSerializer.Deserialize<AiPipelineDefinition>(json, Options)!;
            pipeline = JsonSerializer.Deserialize<AiPipelineDefinition>(JsonSerializer.Serialize(pipeline, Options), Options)!;
            var definition = new DefaultAiConcurrencyDefinitionResolver().Resolve(pipeline, Assert.Single(pipeline.Steps));
            var policy = Assert.Single(definition.Policies);
            Assert.Equal("step.guard", policy.Name);
            Assert.Equal("typescript", policy.ExecutionLanguage);
            Assert.Equal("publication/step.guard/v1", policy.Invocation!.ImplementationRef);
            Assert.Equal(2, ((JsonElement)policy.Config["limit"]!).GetInt32());
        }

        [Fact]
        public async Task Existing_Definition_Snapshot_Service_Preserves_New_Declarations()
        {
            var snapshots = ChildDagCompositionTestData.CreateSnapshotService();
            var definition = new AiPipelineDefinition
            {
                Name = "child", Version = "v1", ExecutionLanguage = "python",
                Steps = new[]
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "format", StepKey = "format", ExecutionLanguage = "typescript",
                        Invocation = new AiInvocationDefinition
                        {
                            Kind = AiInvocationKind.Custom, ImplementationRef = "publication/format/v1"
                        }
                    }
                }
            };
            var snapshot = await snapshots.FreezeDefinitionAsync(definition, "parent");
            var restored = await snapshots.LoadDefinitionAsync(snapshot);
            var step = Assert.Single(restored.Steps);
            Assert.Equal("python", restored.ExecutionLanguage);
            Assert.Equal("typescript", step.ExecutionLanguage);
            Assert.Equal("publication/format/v1", step.Invocation!.ImplementationRef);
            Assert.Equal("typescript", new AiInvocationBindingResolver().ResolveStep(restored, step).ExecutionLanguage);
            Assert.DoesNotContain("InvocationBinding", await snapshots.LoadDefinitionJsonAsync(snapshot));
        }

        [Fact]
        public async Task Existing_Policy_Snapshot_Service_Detaches_Declaration_From_Live_Mutations()
        {
            var snapshots = ChildDagCompositionTestData.CreateSnapshotService();
            var policy = new AiConfiguredPolicyDefinition
            {
                Name = "guard", ExecutionLanguage = "python",
                Invocation = new AiInvocationDefinition
                {
                    Kind = AiInvocationKind.Custom, ImplementationRef = "publication/guard/v1"
                }
            };
            var definition = new AiChildDelegationPolicyDefinition
            {
                Policies = new List<AiConfiguredPolicyDefinition> { policy }
            };
            var snapshot = await snapshots.FreezeDelegationPolicyBindingAsync(definition, "parent");
            policy.Name = "changed";
            policy.ExecutionLanguage = "typescript";
            var restored = await snapshots.LoadDelegationPolicyBindingAsync(snapshot);
            var frozenPolicy = Assert.Single(restored.Policies);
            Assert.Equal("guard", frozenPolicy.Name);
            Assert.Equal("python", frozenPolicy.ExecutionLanguage);
            Assert.Equal("publication/guard/v1", frozenPolicy.Invocation!.ImplementationRef);
        }

        [Fact]
        public void Native_And_Custom_Policies_Preserve_Array_Order()
        {
            const string json = """
            {"enabled":true,"policies":["first",
              {"name":"second","invocation":{"kind":"Custom","implementationRef":"publication/second/v1"},"executionLanguage":"python"},
              {"key":"third","type":"scope"}]}
            """;
            var first = JsonSerializer.Deserialize<AiConcurrencyDefinition>(json, Options)!;
            var second = JsonSerializer.Deserialize<AiConcurrencyDefinition>(JsonSerializer.Serialize(first, Options), Options)!;
            Assert.Equal(new[] { "first", "second", "third" }, second.Policies.Select(x => x.Name));
            Assert.Equal("python", second.Policies[1].ExecutionLanguage);
            Assert.Equal("scope", second.Policies[2].Kind);
        }
    }
}
