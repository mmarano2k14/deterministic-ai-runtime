using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Publication;

namespace Multiplexed.AI.Runtime.Publication
{
    public enum AiDependencyPackageExecutionSupport
    {
        ContractDefined,
        Hosted
    }

    public sealed record AiDependencyPackageCapability(
        AiPublicationDependencyPackageKind Kind,
        string ExecutionLanguage,
        AiDependencyPackageExecutionSupport Support);

    /// <summary>
    /// Finite dependency-packaging capability matrix. Publication captures exact package material;
    /// contract-defined formats fail closed before launch while hosted formats may cross the existing
    /// worker boundary only after language-specific validation.
    /// </summary>
    public static class AiDependencyPackagingContracts
    {
        private static readonly IReadOnlyList<AiDependencyPackageCapability> Values =
            Array.AsReadOnly(new[]
            {
                new AiDependencyPackageCapability(
                    AiPublicationDependencyPackageKind.PythonWheelBundle,
                    AiExecutionLanguages.Python,
                    AiDependencyPackageExecutionSupport.Hosted),
                new AiDependencyPackageCapability(
                    AiPublicationDependencyPackageKind.NodeLockedBundle,
                    AiExecutionLanguages.TypeScript,
                    AiDependencyPackageExecutionSupport.Hosted),
                new AiDependencyPackageCapability(
                    AiPublicationDependencyPackageKind.DotNetAssemblyClosure,
                    AiExecutionLanguages.DotNet,
                    AiDependencyPackageExecutionSupport.Hosted)
            });

        public static IReadOnlyList<AiDependencyPackageCapability> All => Values;

        public static AiDependencyPackageCapability Get(AiPublicationDependencyPackageKind kind) =>
            Values.SingleOrDefault(value => value.Kind == kind)
            ?? throw new NotSupportedException($"Dependency package kind '{kind}' has no runtime contract.");

        internal static AiPublicationDependencyPackage? Capture(
            AiPublicationDependencyPackage? package,
            IReadOnlyList<AiPublicationFileUpload> files,
            AiPublicationEnvironment runtime,
            string dependencyName,
            string dependencyVersion)
        {
            if (package is null) return null;
            Validate(package, files.Select(file => file.Path), runtime.ExecutionLanguage);
            return package.Kind switch
            {
                AiPublicationDependencyPackageKind.PythonWheelBundle =>
                    AiPythonWheelPackaging.Capture(package, files, runtime, dependencyName, dependencyVersion),
                AiPublicationDependencyPackageKind.NodeLockedBundle =>
                    AiNodeLockedPackaging.Capture(package, files, runtime, dependencyName, dependencyVersion),
                AiPublicationDependencyPackageKind.DotNetAssemblyClosure =>
                    AiDotNetAssemblyClosurePackaging.Capture(package, files, runtime, dependencyName, dependencyVersion),
                _ => throw new NotSupportedException($"Dependency package kind '{package.Kind}' has no capture implementation.")
            };
        }

        internal static void Validate(
            AiPublicationDependencyPackage package,
            IEnumerable<string> filePaths,
            string executionLanguage)
        {
            ArgumentNullException.ThrowIfNull(package);
            if (package.SchemaVersion != 1 || !Enum.IsDefined(package.Kind))
                throw new InvalidOperationException("Unsupported deterministic dependency package contract.");

            AiPublicationJson.Path(package.ManifestPath);
            if (!package.ManifestPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A deterministic dependency package manifest must be JSON.");

            var capability = Get(package.Kind);
            if (!string.Equals(capability.ExecutionLanguage, executionLanguage, StringComparison.Ordinal))
                throw new InvalidOperationException("Dependency package kind does not match the effective execution language.");

            var paths = filePaths.ToArray();
            if (paths.Count(path => string.Equals(path, package.ManifestPath, StringComparison.Ordinal)) != 1)
                throw new InvalidOperationException("The deterministic dependency package manifest must be present exactly once.");
        }

        public static void RequireExecutionSupported(
            IReadOnlyList<AiPublicationDependency> dependencies,
            string executionLanguage)
        {
            foreach (var dependency in dependencies)
            {
                if (dependency.Package is null) continue;
                Validate(dependency.Package, dependency.Files.Select(file => file.Path), executionLanguage);
                var capability = Get(dependency.Package.Kind);
                if (capability.Support != AiDependencyPackageExecutionSupport.Hosted)
                    throw new NotSupportedException(
                        $"Dependency package kind '{dependency.Package.Kind}' is immutable and published but hosted execution is not enabled yet.");
            }
        }
    }
}
