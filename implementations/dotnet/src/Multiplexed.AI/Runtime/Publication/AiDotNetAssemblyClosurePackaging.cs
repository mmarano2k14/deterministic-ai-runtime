using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>
    /// Validates the initial managed-only .NET dependency closure. The implementation reads only
    /// supplied immutable assembly bytes and never invokes NuGet, MSBuild, Roslyn or a registry.
    /// </summary>
    internal static partial class AiDotNetAssemblyClosurePackaging
    {
        private const int MaxManifestBytes = 128 * 1024;
        private const int MaxAssemblies = 256;

        [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
        private static partial Regex PackageNameRegex();

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,255}$", RegexOptions.CultureInvariant)]
        private static partial Regex AssemblyNameRegex();

        internal static AiPublicationDependencyPackage Capture(
            AiPublicationDependencyPackage package,
            IReadOnlyList<AiPublicationFileUpload> files,
            AiPublicationEnvironment runtime,
            string dependencyName,
            string dependencyVersion)
        {
            if (package.Kind != AiPublicationDependencyPackageKind.DotNetAssemblyClosure)
                throw new InvalidOperationException(".NET assembly-closure validation received another package kind.");
            if (!string.Equals(runtime.ExecutionLanguage, AiExecutionLanguages.DotNet, StringComparison.Ordinal))
                throw new InvalidOperationException(".NET assembly closures require the .NET execution language.");
            if (!PackageNameRegex().IsMatch(dependencyName))
                throw new InvalidOperationException("A .NET assembly closure requires a portable package name.");
            if (files.Count is < 2 or > MaxAssemblies + 1)
                throw new InvalidOperationException("A .NET assembly closure requires one manifest and a bounded managed assembly set.");

            var manifestFile = files.SingleOrDefault(file =>
                string.Equals(file.Path, package.ManifestPath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("The .NET assembly-closure manifest is missing.");
            var manifest = ReadManifest(manifestFile.Content);
            if (manifest.SchemaVersion != 1)
                throw new InvalidOperationException("Unsupported .NET assembly-closure manifest schema.");
            if (!PackageNameRegex().IsMatch(manifest.PackageName) ||
                !string.Equals(manifest.PackageName, dependencyName, StringComparison.Ordinal) ||
                !string.Equals(manifest.Version, dependencyVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("The .NET assembly-closure manifest identity does not match the dependency identity.");

            AiPublicationJson.Version(manifest.Version);
            var supplied = files
                .Where(file => !string.Equals(file.Path, package.ManifestPath, StringComparison.Ordinal))
                .ToDictionary(file => file.Path, StringComparer.Ordinal);
            var declared = manifest.Assemblies?.ToArray() ?? Array.Empty<AiDotNetAssemblyClosureFile>();
            if (declared.Length == 0 || declared.Length != supplied.Count || declared.Length > MaxAssemblies)
                throw new InvalidOperationException("The .NET assembly-closure manifest must enumerate the complete managed assembly set.");

            var casePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previous = string.Empty;
            foreach (var item in declared)
            {
                ArgumentNullException.ThrowIfNull(item);
                ValidateAssemblyPath(item.Path);
                AiPublicationJson.ValidateHash(item.Sha256);
                ValidateAssemblyName(item.AssemblyName);
                ValidateAssemblyVersion(item.AssemblyVersion);
                if (!casePaths.Add(item.Path))
                    throw new InvalidOperationException("The .NET assembly closure contains duplicate or case-ambiguous paths.");
                if (!assemblyNames.Add(item.AssemblyName))
                    throw new InvalidOperationException("The .NET assembly closure contains ambiguous assembly identities.");
                if (previous.Length != 0 && string.CompareOrdinal(previous, item.Path) >= 0)
                    throw new InvalidOperationException(".NET assembly-closure files must be unique and ordinally sorted.");
                previous = item.Path;
                if (!supplied.TryGetValue(item.Path, out var file))
                    throw new InvalidOperationException("The .NET assembly-closure manifest references missing assembly material.");
                if (!string.Equals(AiPublicationJson.HashBytes(file.Content), item.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("A .NET assembly does not match its manifest digest.");

                var identity = InspectManagedAssembly(file.Content);
                if (!string.Equals(identity.Name, item.AssemblyName, StringComparison.Ordinal) ||
                    !string.Equals(identity.Version, item.AssemblyVersion, StringComparison.Ordinal))
                    throw new InvalidOperationException("A .NET assembly identity does not match its manifest declaration.");
            }

            return package with { };
        }

        private static AiDotNetAssemblyClosureManifest ReadManifest(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
                throw new InvalidOperationException("The .NET assembly-closure manifest exceeds its supported bound.");
            string json;
            try { json = AiPublicationJson.Utf8.GetString(bytes); }
            catch (System.Text.DecoderFallbackException exception)
            {
                throw new InvalidOperationException("The .NET assembly-closure manifest must be UTF-8.", exception);
            }
            AiPublicationJson.ValidateJson(json, MaxManifestBytes);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The .NET assembly-closure manifest must be a JSON object.");
                var expected = new HashSet<string>(new[]
                {
                    "schemaVersion", "packageName", "version", "assemblies"
                }, StringComparer.Ordinal);
                if (root.EnumerateObject().Count() != expected.Count ||
                    root.EnumerateObject().Any(property => !expected.Contains(property.Name)))
                    throw new InvalidOperationException("The .NET assembly-closure manifest contains unexpected fields.");
                if (!root.TryGetProperty("assemblies", out var assemblies) || assemblies.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The .NET assembly-closure manifest assemblies must be an array.");
                foreach (var assembly in assemblies.EnumerateArray())
                {
                    if (assembly.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException(".NET assembly declarations must be objects.");
                    var fields = assembly.EnumerateObject().Select(property => property.Name).ToArray();
                    if (fields.Length != 4 ||
                        !fields.Contains("path", StringComparer.Ordinal) ||
                        !fields.Contains("sha256", StringComparer.Ordinal) ||
                        !fields.Contains("assemblyName", StringComparer.Ordinal) ||
                        !fields.Contains("assemblyVersion", StringComparer.Ordinal))
                        throw new InvalidOperationException("A .NET assembly declaration contains unexpected fields.");
                }
            }
            return AiPublicationJson.Read<AiDotNetAssemblyClosureManifest>(json);
        }

        private static void ValidateAssemblyPath(string path)
        {
            AiPublicationJson.Path(path);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A .NET assembly closure may contain managed DLLs only.");
        }

        private static void ValidateAssemblyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !AssemblyNameRegex().IsMatch(name))
                throw new InvalidOperationException("A portable .NET assembly simple name is required.");
        }

        private static void ValidateAssemblyVersion(string version)
        {
            if (!Version.TryParse(version, out var parsed) || parsed is null || parsed.ToString() != version ||
                parsed.Major < 0 || parsed.Minor < 0 || parsed.Build < 0 || parsed.Revision < 0)
                throw new InvalidOperationException("An exact four-part .NET assembly version is required.");
        }

        private static AssemblyIdentity InspectManagedAssembly(byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null ||
                    (pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0)
                    throw new InvalidOperationException("A .NET assembly closure supports managed IL assemblies only.");
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly)
                    throw new InvalidOperationException("A .NET assembly closure file is not an assembly.");
                var definition = metadata.GetAssemblyDefinition();
                var name = metadata.GetString(definition.Name);
                ValidateAssemblyName(name);
                return new AssemblyIdentity(name, definition.Version.ToString());
            }
            catch (BadImageFormatException exception)
            {
                throw new InvalidOperationException("A .NET assembly closure contains non-managed or malformed assembly bytes.", exception);
            }
        }

        private sealed record AssemblyIdentity(string Name, string Version);
    }
}
