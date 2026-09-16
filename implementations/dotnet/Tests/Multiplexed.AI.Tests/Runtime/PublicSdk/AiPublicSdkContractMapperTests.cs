using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class AiPublicSdkContractMapperTests
    {
        [Fact]
        public void Publication_Mapping_Preserves_Portable_Semantics_Without_Reusing_Public_Clr_Types()
        {
            using var doc = JsonDocument.Parse("{\"limit\":3,\"enabled\":true,\"labels\":[\"a\",\"b\"]}");
            var request = new AiSdkPipelinePublicationRequest
            {
                Definition = new AiSdkPipelineDefinition
                {
                    Name = "orders",
                    Version = "v1",
                    ExecutionLanguage = "python",
                    ExecutionMode = AiSdkExecutionMode.Dag,
                    Config = new Dictionary<string, JsonElement> { ["settings"] = doc.RootElement.Clone() },
                    Steps =
                    [
                        new AiSdkPipelineStepDefinition
                        {
                            Name = "work",
                            StepKey = "custom",
                            Order = 1,
                            Invocation = new AiSdkInvocationDefinition { Kind = AiSdkInvocationKind.Custom },
                            Execution = new AiSdkPipelineStepExecutionDefinition { MaxRetries = 2, RetryDelayMs = 50 }
                        }
                    ]
                },
                Functions =
                [
                    new AiSdkPublicationFunctionUpload
                    {
                        Site = new AiSdkPublicationCallSite { Kind = AiSdkPublicationFunctionKind.Step, StepName = "work" },
                        EnvironmentRef = "python-3.13",
                        EntryPointPath = "main.py",
                        EntryPointSymbol = "run",
                        Sources = [new AiSdkPublicationFileUpload { Path = "main.py", ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("pass")) }]
                    }
                ]
            };

            var mapped = AiPublicSdkContractMapper.ToInternal(request);

            Assert.IsType<AiPipelinePublicationUpload>(mapped);
            Assert.Equal("orders", mapped.Definition.Name);
            Assert.Equal(AiInvocationKind.Custom, Assert.Single(mapped.Definition.Steps).Invocation!.Kind);
            Assert.Equal(2, Assert.Single(mapped.Definition.Steps).Execution!.MaxRetries);
            Assert.Equal("main.py", Assert.Single(Assert.Single(mapped.Functions).Sources).Path);
            Assert.Equal("pass", Encoding.UTF8.GetString(Assert.Single(Assert.Single(mapped.Functions).Sources).Content));
            Assert.IsType<Dictionary<string, object?>>(mapped.Definition.Config["settings"]);
        }

        [Fact]
        public void Unsupported_Public_Schema_Fails_Explicitly()
        {
            var request = new AiSdkPipelinePublicationRequest
            {
                SchemaVersion = 99,
                Definition = new AiSdkPipelineDefinition { Name = "orders" }
            };
            Assert.Throws<NotSupportedException>(() => AiPublicSdkContractMapper.ToInternal(request));
        }

        [Fact]
        public void Invalid_Base64_Fails_Before_Server_Publication()
        {
            var request = new AiSdkPipelinePublicationRequest
            {
                Definition = new AiSdkPipelineDefinition
                {
                    Name = "orders",
                    Steps = [new AiSdkPipelineStepDefinition { Name = "work", StepKey = "custom" }]
                },
                Functions =
                [
                    new AiSdkPublicationFunctionUpload
                    {
                        Site = new AiSdkPublicationCallSite { Kind = AiSdkPublicationFunctionKind.Step, StepName = "work" },
                        EnvironmentRef = "python-3.13",
                        EntryPointPath = "main.py",
                        EntryPointSymbol = "run",
                        Sources = [new AiSdkPublicationFileUpload { Path = "main.py", ContentBase64 = "***" }]
                    }
                ]
            };
            Assert.Throws<FormatException>(() => AiPublicSdkContractMapper.ToInternal(request));
        }
    }
}
