using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>Stable client-visible durable execution-control status.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionControlStatus>))]
    public enum AiSdkExecutionControlStatus
    {
        None,
        Running,
        Pausing,
        Paused,
        Resuming,
        Cancelling,
        Cancelled,
        WaitingForInput
    }
}
