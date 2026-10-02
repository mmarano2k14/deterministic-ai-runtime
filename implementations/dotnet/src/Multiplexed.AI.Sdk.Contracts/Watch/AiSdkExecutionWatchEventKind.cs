using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>Discriminator for one public execution watch stream item.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionWatchEventKind>))]
    public enum AiSdkExecutionWatchEventKind
    {
        Snapshot,
        Event,
        ResyncRequired
    }
}
