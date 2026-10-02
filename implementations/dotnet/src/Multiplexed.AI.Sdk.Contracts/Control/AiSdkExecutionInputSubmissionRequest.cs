using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Public human/external input submission for an execution waiting on a stable waiting key.</summary>
    public sealed record AiSdkExecutionInputSubmissionRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionInputSubmissionRequest;

        [JsonPropertyName("waitingKey")]
        public string WaitingKey { get; init; } = string.Empty;

        [JsonPropertyName("waitingStepName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WaitingStepName { get; init; }

        [JsonPropertyName("input")]
        public JsonElement Input { get; init; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
