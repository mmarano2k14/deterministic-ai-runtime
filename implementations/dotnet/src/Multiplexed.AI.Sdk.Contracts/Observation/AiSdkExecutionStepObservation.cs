using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Observation
{
    /// <summary>Portable observation of one logical step instance.</summary>
    public sealed record AiSdkExecutionStepObservation
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("stepKey")]
        public string StepKey { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public AiSdkExecutionStepStatus Status { get; init; } = AiSdkExecutionStepStatus.Pending;

        [JsonPropertyName("startedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? StartedAtUtc { get; init; }

        [JsonPropertyName("updatedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? UpdatedAtUtc { get; init; }

        [JsonPropertyName("completedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? CompletedAtUtc { get; init; }
    }
}
