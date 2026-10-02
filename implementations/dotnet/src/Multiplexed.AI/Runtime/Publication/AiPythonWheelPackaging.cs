using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Validates the intentionally small pure-Python wheel subset supported by hosted workers.
    /// Validation consumes only supplied immutable bytes; it never invokes pip or a package registry.
    /// </summary>
    internal static partial class AiPythonWheelPackaging
    {
        private const int MaxExpandedBytes = 16 * 1024 * 1024;
        private const int MaxEntryBytes = 4 * 1024 * 1024;
        private const int MaxEntries = 512;

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
        private static partial Regex ImportRootRegex();

        [GeneratedRegex("[-_.]+", RegexOptions.CultureInvariant)]
        private static partial Regex DistributionSeparatorRegex();

        internal static AiPublicationDependencyPackage Capture(
            AiPublicationDependencyPackage package,
            IReadOnlyList<AiPublicationFileUpload> files,
            AiPublicationEnvironment runtime,
            string dependencyName,
            string dependencyVersion)
        {
            if (package.Kind != AiPublicationDependencyPackageKind.PythonWheelBundle)
                throw new InvalidOperationException("Python wheel validation received another package kind.");
            if (!string.Equals(runtime.ExecutionLanguage, AiExecutionLanguages.Python, StringComparison.Ordinal))
                throw new InvalidOperationException("Python wheels require the Python execution language.");
            if (files.Count != 2)
                throw new InvalidOperationException("A Python wheel dependency contains exactly one manifest and one wheel artifact.");

            var manifestFile = files.SingleOrDefault(file =>
                string.Equals(file.Path, package.ManifestPath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The Python wheel manifest is missing.");
            var manifest = ReadManifest(manifestFile.Content);
            if (manifest.SchemaVersion != 1)
                throw new InvalidOperationException("Unsupported Python wheel manifest schema.");

            AiPublicationJson.Path(manifest.WheelPath);
            if (!manifest.WheelPath.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(manifest.WheelPath, package.ManifestPath, StringComparison.Ordinal))
                throw new InvalidOperationException("The Python wheel manifest must reference one wheel artifact.");
            AiPublicationJson.ValidateHash(manifest.WheelSha256);
            AiPublicationJson.Text(manifest.Distribution, nameof(manifest.Distribution));
            AiPublicationJson.Version(manifest.Version);
            if (NormalizeDistribution(manifest.Distribution) != NormalizeDistribution(dependencyName) ||
                !string.Equals(manifest.Version, dependencyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The Python wheel manifest identity does not match the dependency identity.");

            var wheelFile = files.SingleOrDefault(file =>
                string.Equals(file.Path, manifest.WheelPath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The Python wheel artifact is missing.");
            if (!string.Equals(AiPublicationJson.HashBytes(wheelFile.Content), manifest.WheelSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The Python wheel artifact does not match its manifest digest.");

            ValidateWheel(wheelFile.Content, manifest, runtime);
            return package with { };
        }

        private static AiPythonWheelBundleManifest ReadManifest(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > 64 * 1024)
                throw new InvalidOperationException("The Python wheel manifest exceeds its supported bound.");
            var json = AiPublicationJson.Utf8.GetString(bytes);
            AiPublicationJson.ValidateJson(json, 64 * 1024);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The Python wheel manifest must be a JSON object.");
                var expected = new HashSet<string>(new[]
                {
                    "schemaVersion", "wheelPath", "wheelSha256", "distribution", "version", "importRoots"
                }, StringComparer.Ordinal);
                if (root.EnumerateObject().Count() != expected.Count || root.EnumerateObject().Any(p => !expected.Contains(p.Name)))
                    throw new InvalidOperationException("The Python wheel manifest contains unexpected fields.");
            }
            return AiPublicationJson.Read<AiPythonWheelBundleManifest>(json);
        }

        private static void ValidateWheel(byte[] wheelBytes, AiPythonWheelBundleManifest manifest, AiPublicationEnvironment runtime)
        {
            if (!Version.TryParse(runtime.RuntimeVersion, out var runtimeVersion) || runtimeVersion.Major != 3 || runtimeVersion.Minor is not (12 or 13))
                throw new InvalidOperationException("Python wheel execution requires an exact supported CPython 3.12/3.13 runtime identity.");

            using var archive = new ZipArchive(new MemoryStream(wheelBytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
                throw new InvalidOperationException("The Python wheel contains an unsupported number of entries.");

            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                AiPublicationJson.Path(entry.FullName);
                if (!casePaths.Add(entry.FullName))
                    throw new InvalidOperationException("The Python wheel contains duplicate or case-ambiguous paths.");
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                    throw new InvalidOperationException("Symbolic links are not supported in Python wheel bundles.");
                if (entry.Length < 0 || entry.Length > MaxEntryBytes || (expandedBytes += entry.Length) > MaxExpandedBytes)
                    throw new InvalidOperationException("The expanded Python wheel exceeds its supported bound.");
                using var input = entry.Open();
                using var output = new MemoryStream((int)entry.Length);
                input.CopyTo(output);
                if (output.Length != entry.Length)
                    throw new InvalidOperationException("A Python wheel entry changed size while being read.");
                entries.Add(entry.FullName, output.ToArray());
            }

            var wheelMetadataPaths = entries.Keys.Where(path => path.EndsWith(".dist-info/WHEEL", StringComparison.Ordinal)).ToArray();
            if (wheelMetadataPaths.Length != 1)
                throw new InvalidOperationException("A Python wheel bundle requires one .dist-info/WHEEL document.");
            var distInfo = wheelMetadataPaths[0][..^"WHEEL".Length];
            var metadataPath = distInfo + "METADATA";
            var recordPath = distInfo + "RECORD";
            if (!entries.TryGetValue(metadataPath, out var metadataBytes) || !entries.ContainsKey(recordPath))
                throw new InvalidOperationException("A Python wheel bundle requires METADATA and RECORD documents.");

            var wheelMetadata = Utf8(entries[wheelMetadataPaths[0]], "WHEEL");
            if (!HeaderValues(wheelMetadata, "Root-Is-Purelib").Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Only pure-Python wheels are supported.");
            var tags = HeaderValues(wheelMetadata, "Tag").ToArray();
            if (tags.Length == 0 || tags.Any(tag => !CompatibleTag(tag, runtimeVersion)))
                throw new InvalidOperationException("Only pure-Python wheel tags compatible with the pinned runtime are supported.");

            var metadata = Utf8(metadataBytes, "METADATA");
            var names = HeaderValues(metadata, "Name").ToArray();
            var versions = HeaderValues(metadata, "Version").ToArray();
            if (names.Length != 1 || versions.Length != 1 ||
                NormalizeDistribution(names[0]) != NormalizeDistribution(manifest.Distribution) ||
                !string.Equals(versions[0], manifest.Version, StringComparison.Ordinal))
                throw new InvalidOperationException("The Python wheel METADATA identity does not match its manifest.");

            var pythonPaths = new List<string>();
            foreach (var path in entries.Keys)
            {
                if (path.StartsWith(distInfo, StringComparison.Ordinal)) continue;
                if (path.Contains(".data/", StringComparison.Ordinal) || !path.EndsWith(".py", StringComparison.Ordinal))
                    throw new InvalidOperationException("Only Python modules plus wheel metadata are supported in a pure-Python wheel bundle.");
                pythonPaths.Add(path);
                _ = Utf8(entries[path], path);
            }
            if (pythonPaths.Count == 0)
                throw new InvalidOperationException("A Python wheel bundle must contain at least one Python module.");

            var pythonSet = new HashSet<string>(pythonPaths, StringComparer.Ordinal);
            foreach (var path in pythonPaths)
            {
                var parts = path[..^3].Split('/');
                var isPackage = parts[^1] == "__init__";
                var moduleParts = isPackage ? parts[..^1] : parts;
                if (moduleParts.Length == 0 || moduleParts.Any(part => !ImportRootRegex().IsMatch(part)))
                    throw new InvalidOperationException("Python wheel module paths must be ordinary identifier-based modules.");
                for (var index = 1; index < moduleParts.Length; index++)
                {
                    var init = string.Join("/", moduleParts[..index]) + "/__init__.py";
                    if (!pythonSet.Contains(init))
                        throw new InvalidOperationException("Namespace packages are not supported in Python wheel bundles.");
                }
            }

            var roots = pythonPaths.Select(path =>
            {
                var first = path.Split('/')[0];
                return first.EndsWith(".py", StringComparison.Ordinal) ? first[..^3] : first;
            }).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var declared = manifest.ImportRoots?.ToArray() ?? Array.Empty<string>();
            if (declared.Length == 0 || declared.Any(root => !ImportRootRegex().IsMatch(root)) ||
                !declared.SequenceEqual(declared.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal) ||
                declared.Distinct(StringComparer.Ordinal).Count() != declared.Length ||
                !roots.SequenceEqual(declared, StringComparer.Ordinal))
                throw new InvalidOperationException("The Python wheel manifest import roots do not match the wheel payload.");
        }

        private static IEnumerable<string> HeaderValues(string text, string name)
        {
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                var separator = line.IndexOf(':');
                if (separator <= 0 || !line[..separator].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(separator + 1)..].Trim();
                if (value.Length > 0) yield return value;
            }
        }

        private static bool CompatibleTag(string tag, Version runtime) =>
            string.Equals(tag, "py3-none-any", StringComparison.Ordinal) ||
            string.Equals(tag, $"py{runtime.Major}{runtime.Minor}-none-any", StringComparison.Ordinal);

        private static string Utf8(byte[] bytes, string name)
        {
            try { return AiPublicationJson.Utf8.GetString(bytes); }
            catch (DecoderFallbackException exception) { throw new InvalidOperationException($"Python wheel {name} must be UTF-8.", exception); }
        }

        internal static string NormalizeDistribution(string value) =>
            DistributionSeparatorRegex().Replace(value.Trim(), "-").ToLowerInvariant();
    }
}
