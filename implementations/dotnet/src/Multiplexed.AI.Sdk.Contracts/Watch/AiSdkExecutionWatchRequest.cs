using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>
    /// Portable request for an ordered public execution stream. An empty channel set means all public channels.
    /// The public sequence is a resumable cursor and is not an internal storage revision, lease or epoch.
    /// </summary>
    public sealed record AiSdkExecutionWatchRequest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = AiSdkSchemaVersions.ExecutionWatchRequest;

        [JsonPropertyName("executionId")]
        public string ExecutionId { get; init; } = string.Empty;

        [JsonPropertyName("channels")]
        public IReadOnlyList<AiSdkExecutionWatchChannel> Channels { get; init; }
            = Array.Empty<AiSdkExecutionWatchChannel>();

        [JsonPropertyName("afterSequence")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? AfterSequence { get; init; }

        [JsonPropertyName("includeInitialSnapshot")]
        public bool IncludeInitialSnapshot { get; init; } = true;
    }
}
