using System.Reflection;
using System.Text;
using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Publication
{
    /// <summary>Validates portable publication identity and upload material without engine dependencies.</summary>
    public sealed class AiSdkPublicationWireContractTests
    {
        [Fact]
        public void Publication_Request_Roundtrip_Preserves_Nested_CallSite_And_Deterministic_Package()
        {
            var sourceBytes = Encoding.UTF8.GetBytes("export function run() { return 42; }");
            var request = new AiSdkPipelinePublicationRequest
            {
                Definition = new AiSdkPipelineDefinition
                {
                    Name = "root",
                    Version = "v1",
                    Steps =
                    [
                        new AiSdkPipelineStepDefinition { Name = "child", StepKey = "ExecuteChildDag", Order = 0 }
                    ]
                },
                Functions =
                [
                    new AiSdkPublicationFunctionUpload
                    {
                        Site = new AiSdkPublicationCallSite
                        {
                            Kind = AiSdkPublicationFunctionKind.RetryPolicy,
                            StepName = "custom-step",
                            PolicyIndex = 1,
                            DefinitionPath = "/child"
                        },
                        EnvironmentRef = "node-24",
                        EntryPointPath = "src/policy.ts",
                        EntryPointSymbol = "evaluate",
                        Sources =
                        [
                            new AiSdkPublicationFileUpload
                            {
                                Path = "src/policy.ts",
                                ContentBase64 = Convert.ToBase64String(sourceBytes)
                            }
                        ],
                        Dependencies =
                        [
                            new AiSdkPublicationDependencyUpload
                            {
                                Name = "rules",
                                Version = "1.4.0",
                                Files =
                                [
                                    new AiSdkPublicationFileUpload
                                    {
                                        Path = "manifest.json",
                                        ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
                                    }
                                ],
                                Package = new AiSdkPublicationDependencyPackage
                                {
                                    SchemaVersion = 1,
                                    Kind = AiSdkPublicationDependencyPackageKind.NodeLockedBundle,
                                    ManifestPath = "manifest.json"
                                }
                            }
                        ]
                    }
                ]
            };

            var json = JsonSerializer.Serialize(request);
            var restored = JsonSerializer.Deserialize<AiSdkPipelinePublicationRequest>(json);

            Assert.NotNull(restored);
            Assert.Equal(AiSdkSchemaVersions.PipelinePublicationRequest, restored!.SchemaVersion);
            var function = Assert.Single(restored.Functions);
            Assert.Equal(AiSdkPublicationFunctionKind.RetryPolicy, function.Site.Kind);
            Assert.Equal("/child", function.Site.DefinitionPath);
            Assert.Equal(1, function.Site.PolicyIndex);
            Assert.Equal(sourceBytes, Convert.FromBase64String(Assert.Single(function.Sources).ContentBase64));
            Assert.Equal(
                AiSdkPublicationDependencyPackageKind.NodeLockedBundle,
                Assert.Single(function.Dependencies).Package!.Kind);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("RetryPolicy", root.GetProperty("functions")[0].GetProperty("site").GetProperty("kind").GetString());
            Assert.Equal("NodeLockedBundle", root.GetProperty("functions")[0].GetProperty("dependencies")[0]
                .GetProperty("package").GetProperty("kind").GetString());
        }

        [Fact]
        public void Publication_Response_Exposes_Stable_Identity_Without_Storage_Internals()
        {
            var response = new AiSdkPipelinePublicationResponse
            {
                PublicationRef = "publication/root/v1/sha256-abc",
                PublicationSha256 = "abc",
                PipelineName = "root",
                PipelineVersion = "v1"
            };

            var json = JsonSerializer.Serialize(response);

            Assert.Contains("\"publicationRef\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("partition", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("store", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Public_Publication_Contracts_Do_Not_Expose_Byte_Arrays_Or_Object_Typed_Properties()
        {
            var types = typeof(AiSdkPipelinePublicationRequest).Assembly
                .GetExportedTypes()
                .Where(type => type.Namespace?.StartsWith("Multiplexed.AI.Sdk.Contracts.Publication", StringComparison.Ordinal) == true)
                .ToArray();

            Assert.NotEmpty(types);
            Assert.All(
                types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)),
                property =>
                {
                    Assert.NotEqual(typeof(object), property.PropertyType);
                    Assert.NotEqual(typeof(byte[]), property.PropertyType);
                });
        }
    }
}
