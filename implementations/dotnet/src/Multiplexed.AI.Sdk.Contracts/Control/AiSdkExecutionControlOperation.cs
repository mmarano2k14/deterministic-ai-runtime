using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Stable public execution-control operation identifiers.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionControlOperation>))]
    public enum AiSdkExecutionControlOperation
    {
        Pause,
        Resume,
        SubmitInput
    }
}
