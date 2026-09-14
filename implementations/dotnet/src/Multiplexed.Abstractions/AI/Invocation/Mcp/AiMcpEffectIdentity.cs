using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Invocation.Mcp
{
    /// <summary>
    /// Versioned logical-effect metadata. It is not a persistence receipt, a credential,
    /// or a guarantee that a remote tool deduplicates calls. ConnectionRevision remains
    /// on the containing request and participates in RequestDigest.
    /// </summary>
    public sealed record AiMcpEffectIdentity(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("effectId")] string EffectId,
        [property: JsonPropertyName("requestDigest")] string RequestDigest);
}
