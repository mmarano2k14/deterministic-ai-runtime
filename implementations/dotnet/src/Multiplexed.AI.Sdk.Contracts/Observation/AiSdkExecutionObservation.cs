using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Executions;

namespace Multiplexed.AI.Sdk.Contracts.Observation
{
    /// <summary>
    /// Stable snapshot used by polling or future streaming clients. It projects logical execution state only;
    /// control-plane queue records and physical runtime placement are deliberately absent.
    /// </summary>
    public sealed record AiSdkExecutionObservation
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionObservation;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("publicationRef")]
        public string PublicationRef { get; init; } = string.Empty;

        [JsonPropertyName("pipelineName")]
        public string PipelineName { get; init; } = string.Empty;

        [JsonPropertyName("pipelineVersion")]
        public string PipelineVersion { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public AiSdkExecutionStatus Status { get; init; } = AiSdkExecutionStatus.Pending;

        [JsonPropertyName("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; init; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; init; }

        [JsonPropertyName("completedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? CompletedAtUtc { get; init; }

        [JsonPropertyName("steps")]
        public IReadOnlyList<AiSdkExecutionStepObservation> Steps { get; init; }
            = Array.Empty<AiSdkExecutionStepObservation>();
    }
}
