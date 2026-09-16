using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Pipelines
{
    /// <summary>Public orchestration mode. Numeric enum values are not part of the wire contract.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionMode>))]
    public enum AiSdkExecutionMode
    {
        Sequential,
        Dag
    }
}
