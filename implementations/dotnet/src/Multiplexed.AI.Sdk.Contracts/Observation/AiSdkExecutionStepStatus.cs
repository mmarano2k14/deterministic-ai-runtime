using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Observation
{
    /// <summary>
    /// Stable client-visible step lifecycle. Claim ownership, lease expiry and infrastructure recovery counters
    /// remain server-only even when they contribute to this projected state.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionStepStatus>))]
    public enum AiSdkExecutionStepStatus
    {
        Pending,
        Ready,
        Running,
        WaitingForRetry,
        WaitingForExternal,
        Completed,
        Failed
    }
}
