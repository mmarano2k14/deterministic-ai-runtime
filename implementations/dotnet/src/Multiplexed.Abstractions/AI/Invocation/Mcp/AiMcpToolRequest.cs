using System.Text.Json;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation.Mcp
{
    /// <summary>
    /// Internal portable invocation envelope, not MCP JSON-RPC and not a durable dispatch
    /// record. RequestId identifies this attempt; it is NOT an effect idempotency key.
    /// The transport must use the exact server-resolved connection revision and tool.
    /// </summary>
    public sealed record AiMcpToolRequest(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc,
        [property: JsonPropertyName("context")] AiMcpToolInvocationContext Context,
        [property: JsonPropertyName("connectionRef")] string ConnectionRef,
        [property: JsonPropertyName("connectionRevision")] string ConnectionRevision,
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("arguments")] JsonElement Arguments)
    {
        /// <summary>
        /// Required by envelope version 2. Absent in historical version 1 envelopes.
        /// This server-side metadata is not added to MCP tool arguments or HTTP headers.
        /// </summary>
        [JsonPropertyName("effect")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AiMcpEffectIdentity? Effect { get; init; }
    }

    /// <summary>No live context, RBAC snapshot, provider, stores or runtime credentials.</summary>
    public sealed record AiMcpToolInvocationContext(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("tenantGroupId")] string TenantGroupId,
        [property: JsonPropertyName("executionId")] string ExecutionId,
        [property: JsonPropertyName("pipelineName")] string PipelineName,
        [property: JsonPropertyName("pipelineVersion")] string? PipelineVersion,
        [property: JsonPropertyName("stepName")] string StepName,
        [property: JsonPropertyName("stepKey")] string StepKey);
}
