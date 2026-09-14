using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Portable evaluation envelope: scalar identity and a detached JSON configuration.
    /// This is a server-side transport contract, not a public SDK or a durable invocation.
    /// requestId identifies one evaluation, never an external-effect idempotency key.
    /// </summary>
    public sealed record AiConcurrencyPolicyRequest(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("policyName")] string PolicyName,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("ownerStepName")] string? OwnerStepName,
        [property: JsonPropertyName("executionLanguage")] string ExecutionLanguage,
        [property: JsonPropertyName("implementationRef")] string ImplementationRef,
        [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc,
        [property: JsonPropertyName("context")] AiConcurrencyPolicyInput Context,
        [property: JsonPropertyName("config")] JsonElement Config)
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion => 1;

        [JsonPropertyName("policyKind")]
        public string PolicyKind => "concurrency";
    }

    /// <summary>
    /// Explicit admission inputs, copied by the server. No services, stores, lease
    /// authority, credentials, RBAC snapshot or mutable execution state cross the boundary.
    /// Tenant identity is correlation supplied by the trusted runtime, not authorization
    /// granted to a worker. The transport must still enforce publication authorization.
    /// </summary>
    public sealed record AiConcurrencyPolicyInput(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("tenantGroupId")] string? TenantGroupId,
        [property: JsonPropertyName("executionId")] string ExecutionId,
        [property: JsonPropertyName("pipelineKey")] string PipelineKey,
        [property: JsonPropertyName("stepName")] string StepName,
        [property: JsonPropertyName("stepKey")] string StepKey,
        [property: JsonPropertyName("runtimeInstanceId")] string RuntimeInstanceId,
        [property: JsonPropertyName("provider")] string? Provider,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("operation")] string? Operation);
}
