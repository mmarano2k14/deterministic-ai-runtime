using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Executions
{
    /// <summary>
    /// Stable client-visible lifecycle of a durable execution. Numeric enum values are not wire identifiers.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AiSdkExecutionStatus>))]
    public enum AiSdkExecutionStatus
    {
        Pending,
        Running,
        Waiting,
        Completed,
        Failed,
        Cancelled
    }
}
