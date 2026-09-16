using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Pipelines;

namespace Multiplexed.AI.Sdk.Contracts.Publication
{
    /// <summary>
    /// Portable file upload. Content is explicit RFC 4648 Base64 text so the public JSON contract does not
    /// depend on CLR byte-array serialization behavior. Paths are logical publication paths, not host paths.
    /// </summary>
    public sealed record AiSdkPublicationFileUpload
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("contentBase64")]
        public string ContentBase64 { get; init; } = string.Empty;
    }

    /// <summary>Exact dependency label plus immutable files supplied by the client.</summary>
    public sealed record AiSdkPublicationDependencyUpload
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("files")]
        public IReadOnlyList<AiSdkPublicationFileUpload> Files { get; init; }
            = Array.Empty<AiSdkPublicationFileUpload>();

        [JsonPropertyName("package")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkPublicationDependencyPackage? Package { get; init; }
    }

    /// <summary>Code attached to one custom declaration. Server publication generates implementation references.</summary>
    public sealed record AiSdkPublicationFunctionUpload
    {
        [JsonPropertyName("site")]
        public AiSdkPublicationCallSite Site { get; init; } = new();

        [JsonPropertyName("environmentRef")]
        public string EnvironmentRef { get; init; } = string.Empty;

        [JsonPropertyName("entryPointPath")]
        public string EntryPointPath { get; init; } = string.Empty;

        [JsonPropertyName("entryPointSymbol")]
        public string EntryPointSymbol { get; init; } = string.Empty;

        [JsonPropertyName("sources")]
        public IReadOnlyList<AiSdkPublicationFileUpload> Sources { get; init; }
            = Array.Empty<AiSdkPublicationFileUpload>();

        [JsonPropertyName("dependencies")]
        public IReadOnlyList<AiSdkPublicationDependencyUpload> Dependencies { get; init; }
            = Array.Empty<AiSdkPublicationDependencyUpload>();
    }

    /// <summary>Versioned public request for creation of one immutable pipeline publication.</summary>
    public sealed record AiSdkPipelinePublicationRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.PipelinePublicationRequest;

        [JsonPropertyName("definition")]
        public AiSdkPipelineDefinition Definition { get; init; } = new();

        [JsonPropertyName("functions")]
        public IReadOnlyList<AiSdkPublicationFunctionUpload> Functions { get; init; }
            = Array.Empty<AiSdkPublicationFunctionUpload>();
    }

    /// <summary>
    /// Stable public publication identity. Internal partition, payload-store keys and manifest documents are omitted.
    /// </summary>
    public sealed record AiSdkPipelinePublicationResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.PipelinePublicationResponse;

        [JsonPropertyName("publicationRef")]
        public string PublicationRef { get; init; } = string.Empty;

        [JsonPropertyName("publicationSha256")]
        public string PublicationSha256 { get; init; } = string.Empty;

        [JsonPropertyName("pipelineName")]
        public string PipelineName { get; init; } = string.Empty;

        [JsonPropertyName("pipelineVersion")]
        public string PipelineVersion { get; init; } = string.Empty;
    }
}
