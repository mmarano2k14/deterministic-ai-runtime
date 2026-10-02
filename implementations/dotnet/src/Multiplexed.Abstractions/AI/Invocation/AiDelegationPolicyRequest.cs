using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Portable delegation/v1 evaluation envelope. Evaluation occurs before child execution allocation;
    /// the hosted policy cannot allocate a child, mutate the durable relation or choose continuation behavior.
    /// </summary>
    public sealed record AiDelegationPolicyRequest(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("policyName")] string PolicyName,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("ownerStepName")] string? OwnerStepName,
        [property: JsonPropertyName("executionLanguage")] string ExecutionLanguage,
        [property: JsonPropertyName("implementationRef")] string ImplementationRef,
        [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc,
        [property: JsonPropertyName("context")] AiDelegationPolicyInput Context,
        [property: JsonPropertyName("config")] JsonElement Config)
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion => 1;

        [JsonPropertyName("policyKind")]
        public string PolicyKind => "delegation";
    }

    /// <summary>
    /// Detached immutable parent-to-child identity available at the delegation checkpoint.
    /// No ChildExecutionId exists yet because allocation remains a post-approval runtime action.
    /// </summary>
    public sealed record AiDelegationPolicyInput(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("tenantGroupId")] string? TenantGroupId,
        [property: JsonPropertyName("parentExecutionId")] string ParentExecutionId,
        [property: JsonPropertyName("parentCallSiteId")] string ParentCallSiteId,
        [property: JsonPropertyName("childDagId")] string ChildDagId,
        [property: JsonPropertyName("childDagDefinitionVersion")] string ChildDagDefinitionVersion,
        [property: JsonPropertyName("childInvocationKey")] string ChildInvocationKey,
        [property: JsonPropertyName("invocationGeneration")] int InvocationGeneration);
}
