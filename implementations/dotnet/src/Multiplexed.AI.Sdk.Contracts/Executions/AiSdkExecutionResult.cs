using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Executions
{
    /// <summary>Portable terminal result projection for one durable execution.</summary>
    public sealed record AiSdkExecutionResult
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionResult;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public AiSdkExecutionStatus Status { get; init; }

        [JsonPropertyName("output")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Output { get; init; }

        [JsonPropertyName("failure")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkExecutionFailure? Failure { get; init; }

        [JsonPropertyName("completedAtUtc")]
        public DateTimeOffset CompletedAtUtc { get; init; }
    }
}
