using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Executions;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>
    /// Acknowledges cancellation without pretending that request acceptance is the same as terminal cancellation.
    /// The current logical execution status is projected separately from the cancellation-request flag.
    /// </summary>
    public sealed record AiSdkExecutionCancellationResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionCancellationResponse;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("cancellationRequested")]
        public bool CancellationRequested { get; init; }

        [JsonPropertyName("status")]
        public AiSdkExecutionStatus Status { get; init; }

        [JsonPropertyName("requestedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RequestedAtUtc { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
