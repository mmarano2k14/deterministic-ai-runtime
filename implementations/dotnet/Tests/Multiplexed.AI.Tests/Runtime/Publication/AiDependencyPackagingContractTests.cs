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
        public void Capability_Matrix_Is_Finite_And_Not_Yet_Hosted()
        {
            var capabilities = AiDependencyPackagingContracts.All.OrderBy(value => value.Kind).ToArray();

            Assert.Equal(3, capabilities.Length);
            Assert.Equal("python", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.PythonWheelBundle).ExecutionLanguage);
            Assert.Equal("typescript", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.NodeLockedBundle).ExecutionLanguage);
            Assert.Equal("dotnet", capabilities.Single(value =>
                value.Kind == AiPublicationDependencyPackageKind.DotNetAssemblyClosure).ExecutionLanguage);
            Assert.All(capabilities, capability =>
                Assert.Equal(AiDependencyPackageExecutionSupport.ContractDefined, capability.Support));
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
        [InlineData(AiPublicationDependencyPackageKind.PythonWheelBundle, "python")]
        [InlineData(AiPublicationDependencyPackageKind.NodeLockedBundle, "typescript")]
        [InlineData(AiPublicationDependencyPackageKind.DotNetAssemblyClosure, "dotnet")]
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
            var upload = PublicationTestSupport.Upload(language: "python", secondLanguage: null);
            var function = upload.Functions[0];
            var files = new[]
            {
                new AiPublicationFileUpload(
                    "bundle.manifest.json",
                    Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")),
                new AiPublicationFileUpload(
                    "runtime.bin",
                    Encoding.UTF8.GetBytes("runtime"))
            };
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
                    AiPublicationDependencyPackageKind.PythonWheelBundle,
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

        [Theory]
        [InlineData(AiPublicationDependencyPackageKind.PythonWheelBundle, "python")]
        [InlineData(AiPublicationDependencyPackageKind.NodeLockedBundle, "typescript")]
        [InlineData(AiPublicationDependencyPackageKind.DotNetAssemblyClosure, "dotnet")]
        public void Contract_Defined_Packages_Fail_Closed_At_Worker_Projection(
            AiPublicationDependencyPackageKind kind,
            string language)
        {
            var manifest = new AiPublicationFile(
                "bundle.manifest.json",
                new string('a', 64),
                2,
                new AiPublicationDocument("key", new string('b', 64), 2));
            var dependency = new AiPublicationDependency("rules", "1.0.0", new[] { manifest })
            {
                Package = new AiPublicationDependencyPackage(1, kind, manifest.Path)
            };

            Assert.Throws<NotSupportedException>(() =>
                AiDependencyPackagingContracts.RequireExecutionSupported(
                    new[] { dependency },
                    language));
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
            var dependency = function.Dependencies[0] with
            {
                Files = new[]
                {
                    new AiPublicationFileUpload(
                        "bundle.manifest.json",
                        Encoding.UTF8.GetBytes("{\"schemaVersion\":1}")),
                    new AiPublicationFileUpload(
                        "runtime.bin",
                        Encoding.UTF8.GetBytes("runtime"))
                },
                Package = new AiPublicationDependencyPackage(
                    1,
                    kind,
                    "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };
            return upload with { Functions = new[] { function, upload.Functions[1] } };
        }
    }
}
