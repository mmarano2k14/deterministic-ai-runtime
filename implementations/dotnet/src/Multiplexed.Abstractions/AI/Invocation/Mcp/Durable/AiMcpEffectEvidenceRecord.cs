using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Invocation.Mcp;

namespace Multiplexed.Abstractions.AI.Invocation.Mcp.Durable
{
    /// <summary>
    /// Durable lifecycle of one logical outbound MCP effect. Prepared means no physical
    /// outbound attempt has been fenced yet. Dispatching means one attempt owns the
    /// network boundary. NotSent is a confirmed no-emission outcome for that attempt.
    /// </summary>
    public enum AiMcpEffectEvidenceStatus
    {
        Prepared,
        Dispatching,
        Completed,
        Uncertain,
        NotSent
    }

    /// <summary>Tenant ownership used only to address durable effect evidence.</summary>
    public sealed record AiMcpEffectEvidenceScope(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("tenantGroupId")] string TenantGroupId);

    /// <summary>
    /// Immutable logical intent. It contains no endpoint, credential, secret header,
    /// worker identity, claim token, deadline or physical attempt identifier.
    /// </summary>
    public sealed record AiMcpEffectIntent(
        [property: JsonPropertyName("effect")] AiMcpEffectIdentity Effect,
        [property: JsonPropertyName("context")] AiMcpToolInvocationContext Context,
        [property: JsonPropertyName("connectionRef")] string ConnectionRef,
        [property: JsonPropertyName("connectionRevision")] string ConnectionRevision,
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("argumentsJson")] string ArgumentsJson);

    /// <summary>
    /// One physical outbound attempt. RequestId is the existing per-attempt correlation
    /// id and is intentionally distinct from EffectId.
    /// </summary>
    public sealed record AiMcpEffectDispatchAttempt(
        [property: JsonPropertyName("requestId")] string RequestId,
        [property: JsonPropertyName("startedAtUtc")] DateTimeOffset StartedAtUtc,
        [property: JsonPropertyName("deadlineUtc")] DateTimeOffset DeadlineUtc);

    /// <summary>
    /// Confirmed normalized MCP response evidence. IsError is the remote MCP tool result
    /// flag; a confirmed tool error is still a known external outcome.
    /// </summary>
    public sealed record AiMcpEffectResultEvidence(
        [property: JsonPropertyName("isError")] bool IsError,
        [property: JsonPropertyName("responseJson")] string ResponseJson,
        [property: JsonPropertyName("responseSha256")] string ResponseSha256,
        [property: JsonPropertyName("receivedAtUtc")] DateTimeOffset ReceivedAtUtc);

    /// <summary>
    /// Evidence that a dispatched attempt has no confirmed response. The reason code is
    /// audit metadata only; it cannot authorize automatic re-emission.
    /// </summary>
    public sealed record AiMcpEffectUncertaintyEvidence(
        [property: JsonPropertyName("reasonCode")] string ReasonCode,
        [property: JsonPropertyName("recordedAtUtc")] DateTimeOffset RecordedAtUtc);

    /// <summary>
    /// Evidence that the selected physical attempt is confirmed not to have crossed the
    /// business tools/call boundary. This is not itself retry authority.
    /// </summary>
    public sealed record AiMcpEffectNonEmissionEvidence(
        [property: JsonPropertyName("reasonCode")] string ReasonCode,
        [property: JsonPropertyName("recordedAtUtc")] DateTimeOffset RecordedAtUtc);

    /// <summary>
    /// Authoritative durable evidence for one outbound MCP logical effect. Revision is a
    /// storage CAS fence; it is not the DAG claim version and not a worker lease epoch.
    /// </summary>
    public sealed record AiMcpEffectEvidenceRecord
    {
        public int SchemaVersion { get; init; } = 1;
        public required AiMcpEffectEvidenceScope Scope { get; init; }
        public required AiMcpEffectIntent Intent { get; init; }
        public long Revision { get; init; }
        public AiMcpEffectEvidenceStatus Status { get; init; }
        public AiMcpEffectDispatchAttempt? Attempt { get; init; }
        public AiMcpEffectResultEvidence? Result { get; init; }
        public AiMcpEffectUncertaintyEvidence? Uncertainty { get; init; }
        public AiMcpEffectNonEmissionEvidence? NonEmission { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
