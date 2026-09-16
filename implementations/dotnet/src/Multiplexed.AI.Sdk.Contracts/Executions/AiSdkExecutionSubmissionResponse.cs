using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Executions
{
    /// <summary>
    /// Stable acknowledgement of public execution submission. Shared/local queue identities and runtime placement
    /// are server implementation details and are not exposed.
    /// </summary>
    public sealed record AiSdkExecutionSubmissionResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionSubmissionResponse;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("publicationRef")]
        public string PublicationRef { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public AiSdkExecutionStatus Status { get; init; } = AiSdkExecutionStatus.Pending;

        [JsonPropertyName("acceptedAtUtc")]
        public DateTimeOffset AcceptedAtUtc { get; init; }

        [JsonPropertyName("idempotencyKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IdempotencyKey { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
