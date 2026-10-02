using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Replay
{
    /// <summary>
    /// Requests deterministic replay validation of an existing execution. The current public operation is
    /// validation/audit only; it neither restores state nor creates a new durable execution.
    /// </summary>
    public sealed record AiSdkExecutionReplayRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionReplayRequest;

        [JsonPropertyName("strictDeterminism")]
        public bool StrictDeterminism { get; init; } = true;

        [JsonPropertyName("includeDiagnostics")]
        public bool IncludeDiagnostics { get; init; } = true;

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }
    }
}
