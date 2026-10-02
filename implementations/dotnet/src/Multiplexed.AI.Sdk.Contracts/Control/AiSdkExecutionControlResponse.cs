using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Acknowledges one public execution-control request and returns its sanitized durable state.</summary>
    public sealed record AiSdkExecutionControlResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionControlResponse;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("operation")]
        public AiSdkExecutionControlOperation Operation { get; init; }

        [JsonPropertyName("accepted")]
        public bool Accepted { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkExecutionControlState? State { get; init; }

        [JsonPropertyName("acceptedAtUtc")]
        public DateTimeOffset AcceptedAtUtc { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
