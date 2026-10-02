using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Watch
{
    /// <summary>Public semantic event categories available to execution watch subscribers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionWatchChannel>))]
    public enum AiSdkExecutionWatchChannel
    {
        Lifecycle,
        Steps,
        Policies,
        Children,
        Effects,
        Recovery
    }
}
