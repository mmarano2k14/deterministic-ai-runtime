using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Portable retry/v1 evaluation envelope. It carries bounded failure classification inputs only;
    /// retry budget, backoff, jitter and runtime state transitions remain server-owned.
    /// </summary>
    public sealed record AiRetryPolicyRequest(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("policyName")] string PolicyName,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("ownerStepName")] string? OwnerStepName,
        [property: JsonPropertyName("executionLanguage")] string ExecutionLanguage,
        [property: JsonPropertyName("implementationRef")] string ImplementationRef,
        [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc,
        [property: JsonPropertyName("context")] AiRetryPolicyInput Context,
        [property: JsonPropertyName("config")] JsonElement Config)
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion => 1;

        [JsonPropertyName("policyKind")]
        public string PolicyKind => "retry";
    }

    /// <summary>
    /// Detached retry classification context. Raw exception objects, services, stores, execution state,
    /// RBAC snapshots and retry-transition authority never cross the hosted policy boundary.
    /// </summary>
    public sealed record AiRetryPolicyInput(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("tenantGroupId")] string? TenantGroupId,
        [property: JsonPropertyName("executionId")] string ExecutionId,
        [property: JsonPropertyName("pipelineKey")] string PipelineKey,
        [property: JsonPropertyName("stepName")] string StepName,
        [property: JsonPropertyName("stepKey")] string StepKey,
        [property: JsonPropertyName("retryCount")] int RetryCount,
        [property: JsonPropertyName("maxRetries")] int MaxRetries,
        [property: JsonPropertyName("failureReason")] string? FailureReason,
        [property: JsonPropertyName("exceptionType")] string? ExceptionType,
        [property: JsonPropertyName("failedAtUtc")] DateTimeOffset FailedAtUtc);
}
