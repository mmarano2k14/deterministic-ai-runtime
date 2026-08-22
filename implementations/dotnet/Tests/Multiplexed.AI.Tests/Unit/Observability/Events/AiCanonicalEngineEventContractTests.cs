using System.Reflection;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Unit.Observability.Events
{
    /// <summary>
    /// Guards the canonical engine-event declaration contract at repository level.
    /// </summary>
    public sealed class AiCanonicalEngineEventContractTests
    {
        private const string CanonicalNamespace = "Multiplexed.Abstractions.AI.Observability.Events";

        /// <summary>
        /// Verifies that canonical engine-event string declarations live in the single dedicated namespace
        /// and that no two declarations own the same physical semantic value.
        /// </summary>
        [Fact]
        public void Canonical_Event_Declarations_Should_Use_One_Namespace_And_Unique_Physical_Values()
        {
            var declarations = GetCanonicalEventDeclarations();

            Assert.NotEmpty(declarations);
            Assert.All(
                declarations,
                declaration => Assert.Equal(CanonicalNamespace, declaration.DeclaringType.Namespace));

            var duplicates =
                declarations
                    .GroupBy(declaration => declaration.Value, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(
                        group =>
                            $"{group.Key}: {string.Join(", ", group.Select(item => item.DeclaringType.FullName + "." + item.FieldName))}")
                    .ToArray();

            Assert.True(
                duplicates.Length == 0,
                "Canonical engine-event physical values must have exactly one declaration. Duplicates: " +
                string.Join(" | ", duplicates));
        }

        /// <summary>
        /// Verifies that production code and tests consume canonical event declarations instead of
        /// redeclaring their physical string values inline.
        /// </summary>
        [Fact]
        public void Repository_Should_Not_Inline_Canonical_Engine_Event_Strings_Outside_Canonical_Namespace()
        {
            var repositoryRoot = FindRepositoryRoot();
            var canonicalDirectory = Path.GetFullPath(
                Path.Combine(
                    repositoryRoot,
                    "src",
                    "Multiplexed.Abstractions",
                    "AI",
                    "Observability",
                    "Events"));

            var canonicalValues =
                GetCanonicalEventDeclarations()
                    .Select(declaration => declaration.Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(value => value.Length)
                    .ToArray();

            var violations = new List<string>();

            foreach (var searchRootName in new[] { "src", "Tests" })
            {
                var searchRoot = Path.Combine(repositoryRoot, searchRootName);

                foreach (var sourceFile in Directory.EnumerateFiles(searchRoot, "*.cs", SearchOption.AllDirectories))
                {
                    var fullPath = Path.GetFullPath(sourceFile);

                    if (IsGeneratedPath(fullPath) ||
                        fullPath.StartsWith(canonicalDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var source = File.ReadAllText(fullPath);

                    foreach (var canonicalValue in canonicalValues)
                    {
                        if (source.Contains('"' + canonicalValue + '"', StringComparison.Ordinal))
                        {
                            violations.Add(
                                Path.GetRelativePath(repositoryRoot, fullPath) +
                                " -> " +
                                canonicalValue);
                        }
                    }
                }
            }

            Assert.True(
                violations.Count == 0,
                "Canonical engine-event strings must be consumed from the canonical namespace. Violations: " +
                string.Join(" | ", violations));
        }

        /// <summary>
        /// Gets every public constant string declared by a canonical engine-event declaration type.
        /// </summary>
        /// <returns>The canonical event declarations.</returns>
        private static IReadOnlyList<CanonicalEventDeclaration> GetCanonicalEventDeclarations()
        {
            var assembly = typeof(AiEngineEvents).Assembly;
            var declarations = new List<CanonicalEventDeclaration>();

            foreach (var type in assembly.GetTypes().Where(type => string.Equals(type.Namespace, CanonicalNamespace, StringComparison.Ordinal)))
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!field.IsLiteral ||
                        field.FieldType != typeof(string) ||
                        field.GetRawConstantValue() is not string value)
                    {
                        continue;
                    }

                    declarations.Add(
                        new CanonicalEventDeclaration(
                            type,
                            field.Name,
                            value));
                }
            }

            return declarations;
        }

        /// <summary>
        /// Finds the repository root containing the solution file.
        /// </summary>
        /// <returns>The absolute repository root path.</returns>
        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Multiplexed.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException(
                $"Unable to locate repository root from '{AppContext.BaseDirectory}'.");
        }

        /// <summary>
        /// Determines whether a source path belongs to generated build output.
        /// </summary>
        /// <param name="path">The absolute source path.</param>
        /// <returns><c>true</c> for generated build output; otherwise, <c>false</c>.</returns>
        private static bool IsGeneratedPath(string path)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return segments.Any(
                segment =>
                    string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Represents one canonical engine-event constant declaration.
        /// </summary>
        /// <param name="DeclaringType">The declaring type.</param>
        /// <param name="FieldName">The constant field name.</param>
        /// <param name="Value">The physical semantic event value.</param>
        private sealed record CanonicalEventDeclaration(
            Type DeclaringType,
            string FieldName,
            string Value);
    }
}
