using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Publication
{
    /// <summary>Deterministic dependency package forms accepted by the publication boundary.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkPublicationDependencyPackageKind>))]
    public enum AiSdkPublicationDependencyPackageKind
    {
        PythonWheelBundle,
        NodeLockedBundle,
        DotNetAssemblyClosure
    }

    /// <summary>
    /// Identifies a deterministic manifest carried inside one immutable dependency upload.
    /// It never authorizes runtime package-manager activity.
    /// </summary>
    public sealed record AiSdkPublicationDependencyPackage
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("kind")]
        public AiSdkPublicationDependencyPackageKind Kind { get; init; }

        [JsonPropertyName("manifestPath")]
        public string ManifestPath { get; init; } = string.Empty;
    }
}
