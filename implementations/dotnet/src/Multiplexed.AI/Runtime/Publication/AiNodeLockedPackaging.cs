using System.Text.Json;
using System.Text.RegularExpressions;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Validates the intentionally small locked TypeScript dependency subset consumed by the hosted Node worker.
    /// Validation reads only supplied immutable bytes; it never invokes npm, npx or a package registry.
    /// </summary>
    internal static partial class AiNodeLockedPackaging
    {
        private const int MaxManifestBytes = 128 * 1024;
        private const int MaxFiles = 512;

        [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
        private static partial Regex PackageNameRegex();

        internal static AiPublicationDependencyPackage Capture(
            AiPublicationDependencyPackage package,
            IReadOnlyList<AiPublicationFileUpload> files,
            AiPublicationEnvironment runtime,
            string dependencyName,
            string dependencyVersion)
        {
            if (package.Kind != AiPublicationDependencyPackageKind.NodeLockedBundle)
                throw new InvalidOperationException("Node locked-bundle validation received another package kind.");
            if (!string.Equals(runtime.ExecutionLanguage, "typescript", StringComparison.Ordinal))
                throw new InvalidOperationException("Node locked bundles require the TypeScript execution language.");
            if (!PackageNameRegex().IsMatch(dependencyName))
                throw new InvalidOperationException("A locked Node dependency requires a portable package name.");
            if (files.Count is < 2 or > MaxFiles + 1)
                throw new InvalidOperationException("A locked Node dependency requires one manifest and a bounded source closure.");

            var manifestFile = files.SingleOrDefault(file =>
                string.Equals(file.Path, package.ManifestPath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The locked Node dependency manifest is missing.");
            var manifest = ReadManifest(manifestFile.Content);
            if (manifest.SchemaVersion != 1)
                throw new InvalidOperationException("Unsupported locked Node dependency manifest schema.");
            if (!PackageNameRegex().IsMatch(manifest.PackageName) ||
                !string.Equals(manifest.PackageName, dependencyName, StringComparison.Ordinal) ||
                !string.Equals(manifest.Version, dependencyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The locked Node dependency manifest identity does not match the dependency identity.");

            AiPublicationJson.Version(manifest.Version);
            ValidateSourcePath(manifest.EntryPoint, allowDeclaration: false);

            var supplied = files
                .Where(file => !string.Equals(file.Path, package.ManifestPath, StringComparison.Ordinal))
                .ToDictionary(file => file.Path, StringComparer.Ordinal);
            var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declared = manifest.Files?.ToArray() ?? Array.Empty<AiNodeLockedBundleFile>();
            if (declared.Length == 0 || declared.Length != supplied.Count || declared.Length > MaxFiles)
                throw new InvalidOperationException("The locked Node dependency manifest must enumerate the complete source closure.");

            var previous = string.Empty;
            foreach (var item in declared)
            {
                ArgumentNullException.ThrowIfNull(item);
                ValidateSourcePath(item.Path, allowDeclaration: true);
                AiPublicationJson.ValidateHash(item.Sha256);
                if (!casePaths.Add(item.Path))
                    throw new InvalidOperationException("The locked Node dependency contains duplicate or case-ambiguous paths.");
                if (previous.Length != 0 && string.CompareOrdinal(previous, item.Path) >= 0)
                    throw new InvalidOperationException("Locked Node dependency files must be unique and ordinally sorted.");
                previous = item.Path;
                if (!supplied.TryGetValue(item.Path, out var file))
                    throw new InvalidOperationException("The locked Node dependency manifest references missing source material.");
                if (!string.Equals(AiPublicationJson.HashBytes(file.Content), item.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("A locked Node dependency source does not match its manifest digest.");
                _ = Utf8(file.Content, item.Path);
            }

            if (!declared.Any(item => string.Equals(item.Path, manifest.EntryPoint, StringComparison.Ordinal)))
                throw new InvalidOperationException("The locked Node dependency entry point must be part of the declared source closure.");

            return package with { };
        }

        private static AiNodeLockedBundleManifest ReadManifest(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
                throw new InvalidOperationException("The locked Node dependency manifest exceeds its supported bound.");
            var json = Utf8(bytes, "manifest");
            AiPublicationJson.ValidateJson(json, MaxManifestBytes);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The locked Node dependency manifest must be a JSON object.");
                var expected = new HashSet<string>(new[]
                {
                    "schemaVersion", "packageName", "version", "entryPoint", "files"
                }, StringComparer.Ordinal);
                if (root.EnumerateObject().Count() != expected.Count || root.EnumerateObject().Any(property => !expected.Contains(property.Name)))
                    throw new InvalidOperationException("The locked Node dependency manifest contains unexpected fields.");
                if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The locked Node dependency manifest files must be an array.");
                foreach (var file in files.EnumerateArray())
                {
                    if (file.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("Locked Node dependency file declarations must be objects.");
                    var fields = file.EnumerateObject().Select(property => property.Name).ToArray();
                    if (fields.Length != 2 || !fields.Contains("path", StringComparer.Ordinal) || !fields.Contains("sha256", StringComparer.Ordinal))
                        throw new InvalidOperationException("A locked Node dependency file declaration contains unexpected fields.");
                }
            }
            return AiPublicationJson.Read<AiNodeLockedBundleManifest>(json);
        }

        private static void ValidateSourcePath(string path, bool allowDeclaration)
        {
            AiPublicationJson.Path(path);
            if (!path.EndsWith(".ts", StringComparison.Ordinal))
                throw new InvalidOperationException("Locked Node dependency source files must be TypeScript files.");
            if (!allowDeclaration && path.EndsWith(".d.ts", StringComparison.Ordinal))
                throw new InvalidOperationException("A TypeScript declaration file cannot be a locked Node dependency entry point.");
        }

        private static string Utf8(byte[] bytes, string name)
        {
            try { return AiPublicationJson.Utf8.GetString(bytes); }
            catch (System.Text.DecoderFallbackException exception)
            {
                throw new InvalidOperationException($"Locked Node dependency {name} must be UTF-8.", exception);
            }
        }
    }
}
