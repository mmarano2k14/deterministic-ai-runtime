using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Pipelines
{
    /// <summary>
    /// Versioned public pipeline document. This is a wire contract, not the engine's internal pipeline CLR model.
    /// </summary>
    public sealed record AiSdkPipelineDefinition
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.PipelineDefinition;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("version")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Version { get; init; }

        [JsonPropertyName("executionLanguage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutionLanguage { get; init; }

        [JsonPropertyName("executionMode")]
        public AiSdkExecutionMode ExecutionMode { get; init; } = AiSdkExecutionMode.Sequential;

        [JsonPropertyName("steps")]
        public IReadOnlyList<AiSdkPipelineStepDefinition> Steps { get; init; }
            = Array.Empty<AiSdkPipelineStepDefinition>();

        /// <summary>
        /// JSON-portable pipeline configuration. Runtime-specific CLR config types are not exposed to clients.
        /// </summary>
        [JsonPropertyName("config")]
        public IReadOnlyDictionary<string, JsonElement> Config { get; init; }
            = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }
}
