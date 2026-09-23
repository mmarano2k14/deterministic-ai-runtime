using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Stable client-visible pending execution-control action.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionControlAction>))]
    public enum AiSdkExecutionControlAction
    {
        None,
        Pause,
        Resume,
        Cancel,
        WaitForInput,
        SubmitInput
    }
}
