using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>Public resynchronization condition returned when an ordered watch cannot continue safely.</summary>
    public sealed record AiSdkExecutionWatchResyncRequired
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionWatchResyncRequired;

        [JsonPropertyName("reason")]
        public AiSdkExecutionWatchResyncReason Reason { get; init; }
            = AiSdkExecutionWatchResyncReason.HistoryUnavailable;

        [JsonPropertyName("requestedAfterSequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? RequestedAfterSequence { get; init; }

        [JsonPropertyName("earliestAvailableSequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? EarliestAvailableSequence { get; init; }

        [JsonPropertyName("latestSequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? LatestSequence { get; init; }

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; init; }
    }
}
