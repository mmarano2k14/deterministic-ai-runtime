using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Locked TypeScript dependency closures are verified without npm, npx or registry resolution.</summary>
    public sealed class AiNodeLockedPackagingTests
    {
        [Fact]
        public async Task Valid_Locked_Node_Bundle_Is_Captured_Into_The_Immutable_Environment()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            var publication = await fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null)));

            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json =>
                json.Contains("NodeLockedBundle", StringComparison.Ordinal) &&
                json.Contains("bundle.manifest.json", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Locked_Source_Digest_Mismatch_Is_Rejected_Before_Persistence()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                digestOverride: new string('0', 64))));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Theory]
        [InlineData("other", "1.0.0")]
        [InlineData("example", "2.0.0")]
        public async Task Manifest_Identity_Must_Match_The_Dependency(string packageName, string version)
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                packageName: packageName,
                manifestVersion: version)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Entry_Point_Must_Be_Part_Of_The_Locked_Closure()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                entryPoint: "missing.ts")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Declaration_File_Cannot_Be_The_Entry_Point()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                entryPoint: "types.d.ts",
                declarationOnly: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Manifest_Must_Enumerate_Every_Supplied_Source()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                addUndeclaredSource: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Manifest_File_List_Must_Be_Ordinally_Sorted()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                reverseManifestOrder: true,
                includeSecondSource: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Non_TypeScript_Source_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                sourcePath: "index.js")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Unexpected_Manifest_Fields_Are_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(WithBundle(
                PublicationTestSupport.Upload(language: "typescript", secondLanguage: null),
                extraManifestField: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        private static AiPipelinePublicationUpload WithBundle(
            AiPipelinePublicationUpload upload,
            string? packageName = null,
            string? manifestVersion = null,
            string entryPoint = "index.ts",
            string sourcePath = "index.ts",
            string? digestOverride = null,
            bool declarationOnly = false,
            bool addUndeclaredSource = false,
            bool includeSecondSource = false,
            bool reverseManifestOrder = false,
            bool extraManifestField = false)
        {
            var function = upload.Functions[0];
            var original = function.Dependencies[0];
            var source = Encoding.UTF8.GetBytes(declarationOnly
                ? "export declare const value: number;\n"
                : "export const value = 1;\n");
            var sources = new List<AiPublicationFileUpload>
            {
                new(sourcePath, source)
            };
            if (includeSecondSource)
                sources.Add(new AiPublicationFileUpload("lib/math.ts", Encoding.UTF8.GetBytes("export const factor = 2;\n")));
            if (addUndeclaredSource)
                sources.Add(new AiPublicationFileUpload("extra.ts", Encoding.UTF8.GetBytes("export const extra = true;\n")));

            var declared = sources
                .Where(file => !string.Equals(file.Path, "extra.ts", StringComparison.Ordinal))
                .Select(file => new AiNodeLockedBundleFile(
                    file.Path,
                    string.Equals(file.Path, sourcePath, StringComparison.Ordinal) && digestOverride is not null
                        ? digestOverride
                        : Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant()))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToList();
            if (reverseManifestOrder) declared.Reverse();

            byte[] manifest;
            if (extraManifestField)
            {
                var json = JsonSerializer.Serialize(new AiNodeLockedBundleManifest(
                    1,
                    packageName ?? original.Name,
                    manifestVersion ?? original.Version,
                    entryPoint,
                    declared));
                manifest = Encoding.UTF8.GetBytes(json[..^1] + ",\"unexpected\":true}");
            }
            else
            {
                manifest = JsonSerializer.SerializeToUtf8Bytes(new AiNodeLockedBundleManifest(
                    1,
                    packageName ?? original.Name,
                    manifestVersion ?? original.Version,
                    entryPoint,
                    declared));
            }

            var dependency = new AiPublicationDependencyUpload(
                original.Name,
                original.Version,
                new[] { new AiPublicationFileUpload("bundle.manifest.json", manifest) }.Concat(sources).ToArray())
            {
                Package = new AiPublicationDependencyPackage(
                    1,
                    AiPublicationDependencyPackageKind.NodeLockedBundle,
                    "bundle.manifest.json")
            };
            function = function with { Dependencies = new[] { dependency } };
            return upload with { Functions = new[] { function, upload.Functions[1] } };
        }
    }
}
