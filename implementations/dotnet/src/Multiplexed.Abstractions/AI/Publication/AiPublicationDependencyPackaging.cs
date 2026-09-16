using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Publication
{
    /// <summary>
    /// Immutable dependency bundle formats. These values describe packaged input material;
    /// they do not authorize package-manager activity inside a worker.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiPublicationDependencyPackageKind>))]
    public enum AiPublicationDependencyPackageKind
    {
        PythonWheelBundle,
        NodeLockedBundle,
        DotNetAssemblyClosure
    }

    /// <summary>
    /// Identifies the deterministic manifest carried inside one immutable dependency.
    /// The language-specific packaging implementation owns that manifest's schema and validation.
    /// </summary>
    public sealed record AiPublicationDependencyPackage(
        int SchemaVersion,
        AiPublicationDependencyPackageKind Kind,
        string ManifestPath);

    /// <summary>Portable manifest for one immutable pure-Python wheel dependency.</summary>
    public sealed record AiPythonWheelBundleManifest(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("wheelPath")] string WheelPath,
        [property: JsonPropertyName("wheelSha256")] string WheelSha256,
        [property: JsonPropertyName("distribution")] string Distribution,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("importRoots")] IReadOnlyList<string> ImportRoots);

}
