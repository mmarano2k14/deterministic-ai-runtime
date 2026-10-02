using System.Text.Json;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Observation;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>
    /// Stable public envelope emitted by execution watch. It carries either an authoritative public snapshot,
    /// one ordered semantic event, or a resynchronization condition. Internal runtime records are never exposed.
    /// </summary>
    public sealed record AiSdkExecutionWatchEvent
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionWatchEvent;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        /// <summary>
        /// Monotonic public sequence for snapshot/event boundaries. It is deliberately independent from MongoDB
        /// revisions and all lease/epoch implementation details.
        /// </summary>
        [JsonPropertyName("sequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Sequence { get; init; }

        [JsonPropertyName("kind")]
        public AiSdkExecutionWatchEventKind Kind { get; init; } = AiSdkExecutionWatchEventKind.Event;

        [JsonPropertyName("occurredAtUtc")]
        public DateTimeOffset OccurredAtUtc { get; init; }

        [JsonPropertyName("channel")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkExecutionWatchChannel? Channel { get; init; }

        [JsonPropertyName("eventType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EventType { get; init; }

        [JsonPropertyName("payloadSchemaVersion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PayloadSchemaVersion { get; init; }

        /// <summary>
        /// Versioned public event payload. Server projection must construct this value explicitly rather than
        /// serializing internal runtime records into the public stream.
        /// </summary>
        [JsonPropertyName("payload")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? Payload { get; init; }

        [JsonPropertyName("snapshot")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkExecutionObservation? Snapshot { get; init; }

        [JsonPropertyName("resyncRequired")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiSdkExecutionWatchResyncRequired? ResyncRequired { get; init; }
    }
}
