using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Public cooperative cancellation request for one durable execution.</summary>
    public sealed record AiSdkExecutionCancellationRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionCancellationRequest;

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
