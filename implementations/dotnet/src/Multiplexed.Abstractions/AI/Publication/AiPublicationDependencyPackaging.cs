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
}
