using System.Reflection;
using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Pipelines;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Pipelines
{
    /// <summary>Validates the public pipeline wire shape independently from engine CLR models.</summary>
    public sealed class AiSdkPipelineWireContractTests
    {
        [Fact]
        public void Pipeline_Roundtrip_Preserves_Portable_Invocation_And_Json_Config()
        {
            var definition = new AiSdkPipelineDefinition
            {
                Name = "analysis",
                Version = "v7",
                ExecutionLanguage = "python",
                ExecutionMode = AiSdkExecutionMode.Dag,
                Config = new Dictionary<string, JsonElement>
                {
                    ["region"] = JsonSerializer.SerializeToElement("eu")
                },
                Steps =
                [
                    new AiSdkPipelineStepDefinition
                    {
                        Name = "tool",
                        StepKey = "mcp.tool",
                        Order = 1,
                        DependsOn = ["prepare"],
                        Invocation = new AiSdkInvocationDefinition
                        {
                            Kind = AiSdkInvocationKind.Mcp,
                            ConnectionRef = "crm",
                            Tool = "ticket.create"
                        },
                        Input = new Dictionary<string, JsonElement>
                        {
                            ["priority"] = JsonSerializer.SerializeToElement(3)
                        },
                        Execution = new AiSdkPipelineStepExecutionDefinition
                        {
                            MaxRetries = 2,
                            RetryDelayMs = 250
                        }
                    }
                ]
            };

            var json = JsonSerializer.Serialize(definition);
            var restored = JsonSerializer.Deserialize<AiSdkPipelineDefinition>(json);

            Assert.NotNull(restored);
            Assert.Equal(AiSdkSchemaVersions.PipelineDefinition, restored!.SchemaVersion);
            Assert.Equal(AiSdkExecutionMode.Dag, restored.ExecutionMode);
            var step = Assert.Single(restored.Steps);
            Assert.Equal(AiSdkInvocationKind.Mcp, step.Invocation!.Kind);
            Assert.Equal("crm", step.Invocation.ConnectionRef);
            Assert.Equal("ticket.create", step.Invocation.Tool);
            Assert.Equal(3, step.Input["priority"].GetInt32());
            Assert.Equal("eu", restored.Config["region"].GetString());
            Assert.Equal(2, step.Execution!.MaxRetries);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("Dag", root.GetProperty("executionMode").GetString());
            Assert.Equal("Mcp", root.GetProperty("steps")[0].GetProperty("invocation").GetProperty("kind").GetString());
            Assert.False(root.TryGetProperty("ExecutionMode", out _));
        }

        [Fact]
        public void Public_Pipeline_Contracts_Do_Not_Expose_Object_Typed_Properties()
        {
            var types = typeof(AiSdkPipelineDefinition).Assembly
                .GetExportedTypes()
                .Where(type => type.Namespace?.StartsWith("Multiplexed.AI.Sdk.Contracts.Pipelines", StringComparison.Ordinal) == true)
                .ToArray();

            Assert.NotEmpty(types);
            Assert.All(
                types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)),
                property => Assert.NotEqual(typeof(object), property.PropertyType));
        }
    }
}
