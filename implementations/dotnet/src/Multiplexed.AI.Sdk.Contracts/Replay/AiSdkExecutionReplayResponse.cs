using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Replay
{
    /// <summary>Sanitized deterministic replay-validation result.</summary>
    public sealed record AiSdkExecutionReplayResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionReplayResponse;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("succeeded")]
        public bool Succeeded { get; init; }

        [JsonPropertyName("deterministic")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Deterministic { get; init; }

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; init; }

        [JsonPropertyName("diagnostics")]
        public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

        [JsonPropertyName("failureReason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FailureReason { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }

        [JsonPropertyName("startedAtUtc")]
        public DateTimeOffset StartedAtUtc { get; init; }

        [JsonPropertyName("completedAtUtc")]
        public DateTimeOffset CompletedAtUtc { get; init; }

        [JsonPropertyName("durationMs")]
        public long DurationMs { get; init; }
    }
}
