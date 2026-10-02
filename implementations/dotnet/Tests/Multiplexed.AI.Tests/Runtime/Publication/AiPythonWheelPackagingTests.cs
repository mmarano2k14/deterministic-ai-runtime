using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Tests.Runtime.Publication
{
    /// <summary>Pure-Python wheels are captured and structurally verified without invoking pip or a registry.</summary>
    public sealed class AiPythonWheelPackagingTests
    {
        [Fact]
        public async Task Valid_Pure_Python_Wheel_Is_Captured_Into_The_Immutable_Environment()
        {
            using var fixture = new PublicationTestSupport.Fixture();
            UsePythonRuntime(fixture);
            var publication = await fixture.PublishAsync(WithWheel(PublicationTestSupport.Upload(secondLanguage: null)));

            Assert.StartsWith("pub-", publication.PublicationRef);
            Assert.Contains(fixture.MemoryPayloads.Documents.Values, json =>
                json.Contains("PythonWheelBundle", StringComparison.Ordinal) &&
                json.Contains("rules-2.0.1-py3-none-any.whl", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Wheel_Digest_Mismatch_Is_Rejected_Before_Persistence()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            var upload = WithWheel(PublicationTestSupport.Upload(secondLanguage: null), manifestHash: new string('0', 64));

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(upload));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Native_Extension_In_Wheel_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), native: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Non_Purelib_Wheel_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), purelib: false)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Wheel_Path_Traversal_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), traversal: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Namespace_Package_Is_Rejected()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), namespacePackage: true)));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Manifest_Import_Roots_Must_Match_Wheel_Content()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), importRoot: "other_rules")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Wheel_Tag_Must_Match_The_Pinned_Runtime()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture, "3.13.5");
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), wheelTag: "py312-none-any")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        [Fact]
        public async Task Wheel_Metadata_Identity_Must_Match_Dependency()
        {
            using var fixture = new PublicationTestSupport.Fixture(); UsePythonRuntime(fixture);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PublishAsync(
                WithWheel(PublicationTestSupport.Upload(secondLanguage: null), metadataName: "other-rules")));
            Assert.Empty(fixture.MemoryPayloads.Writes);
        }

        private static void UsePythonRuntime(PublicationTestSupport.Fixture fixture, string version = "3.13.5")
        {
            var current = fixture.Environments.Entries["python-fixed"];
            fixture.Environments.Entries["python-fixed"] = current with { RuntimeVersion = version };
        }

        private static AiPipelinePublicationUpload WithWheel(
            AiPipelinePublicationUpload upload,
            bool native = false,
            bool purelib = true,
            bool traversal = false,
            bool namespacePackage = false,
            string wheelTag = "py3-none-any",
            string importRoot = "wheel_rules",
            string metadataName = "rules",
            string? manifestHash = null)
        {
            const string dependencyName = "rules";
            const string dependencyVersion = "2.0.1";
            var wheelPath = $"rules-{dependencyVersion}-{wheelTag}.whl";
            var wheel = Wheel(native, purelib, traversal, namespacePackage, wheelTag, metadataName, dependencyVersion);
            var digest = manifestHash ?? Convert.ToHexString(SHA256.HashData(wheel)).ToLowerInvariant();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new AiPythonWheelBundleManifest(
                1, wheelPath, digest, dependencyName, dependencyVersion, new[] { importRoot }));
            var dependency = new AiPublicationDependencyUpload(
                dependencyName,
                dependencyVersion,
                new[]
                {
                    new AiPublicationFileUpload("bundle.manifest.json", manifest),
                    new AiPublicationFileUpload(wheelPath, wheel)
                })
            {
                Package = new AiPublicationDependencyPackage(
                    1, AiPublicationDependencyPackageKind.PythonWheelBundle, "bundle.manifest.json")
            };
            var first = upload.Functions[0] with { Dependencies = new[] { dependency } };
            return upload with { Functions = new[] { first, upload.Functions[1] } };
        }

        private static byte[] Wheel(
            bool native,
            bool purelib,
            bool traversal,
            bool namespacePackage,
            string tag,
            string metadataName,
            string version)
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var modulePath = traversal ? "../escape.py" : namespacePackage ? "wheel_rules/math.py" : "wheel_rules/__init__.py";
                Text(archive, modulePath, "def transform(n):\n    return n * 4\n");
                if (native) Binary(archive, "wheel_rules/native.pyd", new byte[] { 1, 2, 3 });
                var distInfo = $"rules-{version}.dist-info/";
                Text(archive, distInfo + "WHEEL",
                    "Wheel-Version: 1.0\nGenerator: deterministic-test\nRoot-Is-Purelib: " +
                    (purelib ? "true" : "false") + "\nTag: " + tag + "\n");
                Text(archive, distInfo + "METADATA",
                    "Metadata-Version: 2.1\nName: " + metadataName + "\nVersion: " + version + "\n");
                Text(archive, distInfo + "RECORD", string.Empty);
            }
            return stream.ToArray();
        }

        private static void Text(ZipArchive archive, string path, string value) =>
            Binary(archive, path, Encoding.UTF8.GetBytes(value));

        private static void Binary(ZipArchive archive, string path, byte[] value)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var output = entry.Open();
            output.Write(value);
        }
    }
}
