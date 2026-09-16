using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Managed .NET dependency closures are frozen and verified without NuGet restore or runtime compilation.</summary>
    public sealed class AiDotNetAssemblyPackagingTests
    {
        [Fact]
        public async Task Valid_Managed_Assembly_Closure_Is_Captured_Into_The_Immutable_Environment()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null)));

            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json =>
                json.Contains("DotNetAssemblyClosure", StringComparison.Ordinal) &&
                json.Contains("bundle.manifest.json", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Assembly_Digest_Mismatch_Is_Rejected_Before_Persistence()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                digestOverride: new string('0', 64))));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("other", "1.0.0")]
        [InlineData("example", "2.0.0")]
        public async Task Manifest_Identity_Must_Match_The_Dependency(string packageName, string version)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                packageName: packageName,
                manifestVersion: version)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("Other.Assembly", null)]
        [InlineData(null, "9.9.9.9")]
        public async Task Manifest_Assembly_Identity_Must_Match_The_Managed_Bytes(string? name, string? version)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                assemblyNameOverride: name,
                assemblyVersionOverride: version)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Non_Managed_Dll_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                assemblyBytesOverride: Encoding.UTF8.GetBytes("not-a-managed-assembly"))));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Manifest_Must_Enumerate_The_Complete_Assembly_Set()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                addUndeclaredAssembly: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Managed_Assembly_Paths_Must_Be_Dlls()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                assemblyPath: "dependency.bin")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Unexpected_Manifest_Fields_Are_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithClosure(
                PublicationTestSupport.Upload(language: "dotnet", secondLanguage: null),
                extraManifestField: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        private static AiPipelinePublicationUpload WithClosure(
            AiPipelinePublicationUpload upload,
            string? packageName = null,
            string? manifestVersion = null,
            string assemblyPath = "Multiplexed.AI.HostedInvocation.TestDependency.dll",
            string? digestOverride = null,
            string? assemblyNameOverride = null,
            string? assemblyVersionOverride = null,
            byte[]? assemblyBytesOverride = null,
            bool addUndeclaredAssembly = false,
            bool extraManifestField = false)
        {
            var function = upload.Functions[0];
            var original = function.Dependencies[0];
            var bytes = assemblyBytesOverride ?? FixtureAssemblyBytes();
            var identity = assemblyBytesOverride is null ? ReadIdentity(bytes) : (Name: "Invalid.Managed.Assembly", Version: "1.0.0.0");
            var declaration = new AiDotNetAssemblyClosureFile(
                assemblyPath,
                digestOverride ?? Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                assemblyNameOverride ?? identity.Name,
                assemblyVersionOverride ?? identity.Version);

            byte[] manifest;
            if (extraManifestField)
            {
                var json = JsonSerializer.Serialize(new AiDotNetAssemblyClosureManifest(
                    1,
                    packageName ?? original.Name,
                    manifestVersion ?? original.Version,
                    new[] { declaration }));
                manifest = Encoding.UTF8.GetBytes(json[..^1] + ",\"unexpected\":true}");
            }
            else
            {
                manifest = JsonSerializer.SerializeToUtf8Bytes(new AiDotNetAssemblyClosureManifest(
                    1,
                    packageName ?? original.Name,
                    manifestVersion ?? original.Version,
                    new[] { declaration }));
            }

            var files = new List<AiPublicationFileUpload>
            {
                new("bundle.manifest.json", manifest),
                new(assemblyPath, bytes)
            };
            if (addUndeclaredAssembly)
                files.Add(new AiPublicationFileUpload("extra.dll", FixtureAssemblyBytes()));

            var dependency = new AiPublicationDependencyUpload(original.Name, original.Version, files)
            {
                Package = new AiPublicationDependencyPackage(
                    1,
                    AiPublicationDependencyPackageKind.DotNetAssemblyClosure,
                    "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };
            return upload with { Functions = new[] { function, upload.Functions[1] } };
        }

        private static byte[] FixtureAssemblyBytes()
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "dotnet-worker-fixture",
                "Multiplexed.AI.HostedInvocation.TestDependency.dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("Build the test project so the managed dependency fixture is produced.", path);
            return File.ReadAllBytes(path);
        }

        private static (string Name, string Version) ReadIdentity(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var metadata = pe.GetMetadataReader();
            var definition = metadata.GetAssemblyDefinition();
            return (metadata.GetString(definition.Name), definition.Version.ToString());
        }
    }
}
