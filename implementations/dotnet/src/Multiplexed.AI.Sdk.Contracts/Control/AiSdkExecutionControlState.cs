using System.Text.Json.Serialization;

namespace Multiplexed.AI.Sdk.Contracts.Control
{
    /// <summary>
    /// Public projection of durable execution-control state. Internal CAS versions, runtime identities and
    /// submitted input payloads are deliberately excluded.
    /// </summary>
    public sealed record AiSdkExecutionControlState
    {
        [JsonPropertyName("status")]
        public AiSdkExecutionControlStatus Status { get; init; }

        [JsonPropertyName("pendingAction")]
        public AiSdkExecutionControlAction PendingAction { get; init; }

        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        [JsonPropertyName("waitingKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WaitingKey { get; init; }

        [JsonPropertyName("waitingStepName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WaitingStepName { get; init; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; init; }

        [JsonPropertyName("pauseRequestedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? PauseRequestedAtUtc { get; init; }

        [JsonPropertyName("pausedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? PausedAtUtc { get; init; }

        [JsonPropertyName("resumeRequestedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ResumeRequestedAtUtc { get; init; }

        [JsonPropertyName("inputReceivedAtUtc")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? InputReceivedAtUtc { get; init; }
    }
}
