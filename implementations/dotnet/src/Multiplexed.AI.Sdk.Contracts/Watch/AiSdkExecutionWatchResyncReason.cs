using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>Portable reasons that require an authoritative snapshot before watch can continue.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionWatchResyncReason>))]
    public enum AiSdkExecutionWatchResyncReason
    {
        HistoryUnavailable,
        GapDetected,
        InvalidCursor
    }
}
