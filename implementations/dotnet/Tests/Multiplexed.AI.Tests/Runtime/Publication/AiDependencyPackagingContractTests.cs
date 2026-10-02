using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Freezes deterministic dependency bundle identity before language-specific consumption is enabled.</summary>
    public sealed class AiDependencyPackagingContractTests
    {
        [Fact]
        public void Capability_Matrix_Promotes_All_Selected_Package_Kinds_To_Hosted()
        {
            var capabilities = AiDependencyPackagingContracts.All.OrderBy(value => value.Kind).ToArray();

            Assert.Equal(3, capabilities.Length);
            Assert.Equal("python", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.PythonWheelBundle).ExecutionLanguage);
            Assert.Equal("typescript", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.NodeLockedBundle).ExecutionLanguage);
            Assert.Equal("dotnet", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.DotNetAssemblyClosure).ExecutionLanguage);
            Assert.Equal(AiDependencyPackageExecutionSupport.Hosted, capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.PythonWheelBundle).Support);
            Assert.Equal(AiDependencyPackageExecutionSupport.Hosted, capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.NodeLockedBundle).Support);
            Assert.Equal(AiDependencyPackageExecutionSupport.Hosted, capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.DotNetAssemblyClosure).Support);
        }

        [Fact]
        public void Historical_Explicit_File_Dependency_Omits_Package_Metadata()
        {
            var dependency = new AiPublicationDependencyUpload(
                "rules",
                "1.0.0",
                new[] { new AiPublicationFileUpload("rules.dat", new byte[] { 1 }) });

            var json = JsonSerializer.Serialize(dependency);

            Assert.DoesNotContain("Package", json, StringComparison.Ordinal);
            Assert.Null(dependency.Package);
        }

        [Theory]
        [InlineData(AiPublicationDependencyPackageKind.NodeLockedBundle, "typescript")]
        public async Task Matching_Package_Kind_Is_Captured_Into_Immutable_Environment(
            AiPublicationDependencyPackageKind kind,
            string language)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = WithPackage(PublicationTestSupport.Upload(language: language, secondLanguage: null), kind);
            var publication = await fixture.PublishAsync(upload);

            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json =>
                json.Contains("\"Package\"", StringComparison.Ordinal) &&
                json.Contains(kind.ToString(), StringComparison.Ordinal) &&
                json.Contains("bundle.manifest.json", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(AiPublicationDependencyPackageKind.PythonWheelBundle, "typescript")]
        [InlineData(AiPublicationDependencyPackageKind.PythonWheelBundle, "dotnet")]
        [InlineData(AiPublicationDependencyPackageKind.NodeLockedBundle, "python")]
        [InlineData(AiPublicationDependencyPackageKind.NodeLockedBundle, "dotnet")]
        [InlineData(AiPublicationDependencyPackageKind.DotNetAssemblyClosure, "python")]
        [InlineData(AiPublicationDependencyPackageKind.DotNetAssemblyClosure, "typescript")]
        public async Task Package_Kind_Cannot_Cross_Execution_Languages(
            AiPublicationDependencyPackageKind kind,
            string language)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = WithPackage(PublicationTestSupport.Upload(language: language, secondLanguage: null), kind);

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Package_Manifest_Must_Be_Present_Exactly_Once()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: "python", secondLanguage: null);
            var function = upload.Functions[0];
            var dependency = function.Dependencies[0] with
            {
                Files = new[] { new AiPublicationFileUpload("runtime.py", Encoding.UTF8.GetBytes("value = 1")) },
                Package = new AiPublicationDependencyPackage(
                    1,
                    AiPublicationDependencyPackageKind.PythonWheelBundle,
                    "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData(0, "bundle.manifest.json")]
        [InlineData(2, "bundle.manifest.json")]
        [InlineData(1, "bundle.manifest.txt")]
        [InlineData(1, "../bundle.manifest.json")]
        public async Task Unsupported_Package_Contract_Is_Rejected_Before_Persistence(
            int schemaVersion,
            string manifestPath)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: "python", secondLanguage: null);
            var function = upload.Functions[0];
            var dependency = function.Dependencies[0] with
            {
                Files = new[]
                {
                    new AiPublicationFileUpload(
                        manifestPath == "../bundle.manifest.json" ? "bundle.manifest.json" : manifestPath,
                        Encoding.UTF8.GetBytes("{}"))
                },
                Package = new AiPublicationDependencyPackage(
                    schemaVersion,
                    AiPublicationDependencyPackageKind.PythonWheelBundle,
                    manifestPath)
            };
            function = function with { Dependencies = new[] { dependency } };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.PublishAsync(upload with { Functions = new[] { function, upload.Functions[1] } }));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Package_Descriptor_Alone_Changes_Immutable_Publication_Identity()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var upload = PublicationTestSupport.Upload(language: "typescript", secondLanguage: null);
            var function = upload.Functions[0];
            var files = NodeBundleFiles(function.Dependencies[0].Name, function.Dependencies[0].Version);
            function = function with
            {
                Dependencies = new[]
                {
                    function.Dependencies[0] with { Files = files }
                }
            };
            var explicitFiles = upload with { Functions = new[] { function, upload.Functions[1] } };
            var baseline = await fixture.PublishAsync(explicitFiles);

            var dependency = function.Dependencies[0] with
            {
                Package = new AiPublicationDependencyPackage(
                    1,
                    AiPublicationDependencyPackageKind.NodeLockedBundle,
                    "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };
            var packaged = await fixture.PublishAsync(
                upload with { Functions = new[] { function, upload.Functions[1] } });

            Assert.NotEqual(baseline.PublicationRef, packaged.PublicationRef);
            Assert.NotEqual(
                baseline.Manifest.Functions[0].Environment.Sha256,
                packaged.Manifest.Functions[0].Environment.Sha256);
        }

        [Fact]
        public void DotNet_Assembly_Closure_Metadata_Is_Execution_Supported()
        {
            var manifest = new AiPublicationFile(
                "bundle.manifest.json",
                new string('a', 64),
                2,
                new AiPublicationDocument("key", new string('b', 64), 2));
            var dependency = new AiPublicationDependency("rules", "1.0.0", new[] { manifest })
            {
                Package = new AiPublicationDependencyPackage(
                    1, AiPublicationDependencyPackageKind.DotNetAssemblyClosure, manifest.Path)
            };

            AiDependencyPackagingContracts.RequireExecutionSupported(
                new[] { dependency },
                "dotnet");
        }

        [Fact]
        public void Python_Wheel_Package_Metadata_Is_Execution_Supported()
        {
            var manifest = new AiPublicationFile(
                "bundle.manifest.json", new string('a', 64), 2,
                new AiPublicationDocument("key-a", new string('b', 64), 2));
            var wheel = new AiPublicationFile(
                "rules-2.0.1-py3-none-any.whl", new string('c', 64), 2,
                new AiPublicationDocument("key-c", new string('d', 64), 2));
            var dependency = new AiPublicationDependency("rules", "2.0.1", new[] { manifest, wheel })
            {
                Package = new AiPublicationDependencyPackage(
                    1, AiPublicationDependencyPackageKind.PythonWheelBundle, manifest.Path)
            };

            AiDependencyPackagingContracts.RequireExecutionSupported(new[] { dependency }, "python");
        }

        [Fact]
        public void Node_Locked_Package_Metadata_Is_Execution_Supported()
        {
            var manifest = new AiPublicationFile(
                "bundle.manifest.json", new string('a', 64), 2,
                new AiPublicationDocument("key-a", new string('b', 64), 2));
            var source = new AiPublicationFile(
                "index.ts", new string('c', 64), 2,
                new AiPublicationDocument("key-c", new string('d', 64), 2));
            var dependency = new AiPublicationDependency("rules", "2.0.1", new[] { manifest, source })
            {
                Package = new AiPublicationDependencyPackage(
                    1, AiPublicationDependencyPackageKind.NodeLockedBundle, manifest.Path)
            };

            AiDependencyPackagingContracts.RequireExecutionSupported(new[] { dependency }, "typescript");
        }

        [Fact]
        public void Historical_Explicit_File_Dependencies_Remain_Executable_By_Existing_Workers()
        {
            var dependency = new AiPublicationDependency(
                "rules",
                "1.0.0",
                new[]
                {
                    new AiPublicationFile(
                        "rules.py",
                        new string('a', 64),
                        1,
                        new AiPublicationDocument("key", new string('b', 64), 1))
                });

            AiDependencyPackagingContracts.RequireExecutionSupported(
                new[] { dependency },
                "python");
        }

        private static AiPipelinePublicationUpload WithPackage(
            AiPipelinePublicationUpload upload,
            AiPublicationDependencyPackageKind kind)
        {
            var function = upload.Functions[0];
            var current = function.Dependencies[0];
            var files = kind == AiPublicationDependencyPackageKind.NodeLockedBundle
                ? NodeBundleFiles(current.Name, current.Version)
                : new[]
                {
                    new AiPublicationFileUpload(
                        "bundle.manifest.json",
                        Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")),
                    new AiPublicationFileUpload(
                        "runtime.bin",
                        Encoding.UTF8.GetBytes("runtime"))
                };
            var dependency = current with
            {
                Files = files,
                Package = new AiPublicationDependencyPackage(1, kind, "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };
            return upload with { Functions = new[] { function, upload.Functions[1] } };
        }

        private static AiPublicationFileUpload[] NodeBundleFiles(string name, string version)
        {
            var source = Encoding.UTF8.GetBytes("export const value = 1;\n");
            var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new AiNodeLockedBundleManifest(
                1,
                name,
                version,
                "index.ts",
                new[] { new AiNodeLockedBundleFile("index.ts", sourceHash) }));
            return new[]
            {
                new AiPublicationFileUpload("bundle.manifest.json", manifest),
                new AiPublicationFileUpload("index.ts", source)
            };
        }
    }
}
