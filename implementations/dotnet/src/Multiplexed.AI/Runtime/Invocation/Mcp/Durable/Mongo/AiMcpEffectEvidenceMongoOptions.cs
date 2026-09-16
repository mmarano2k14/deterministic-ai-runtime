namespace Multiplexed.AI.Runtime.Invocation.Mcp.Durable.Mongo
{
    /// <summary>
    /// Uses the host's existing Mongo database/client lifetime. Required uniqueness and
    /// reconciliation indexes cannot be disabled for authoritative MCP effect evidence.
    /// </summary>
    public sealed class AiMcpEffectEvidenceMongoOptions
    {
        public string CollectionName { get; set; } = "ai_mcp_effect_evidence";
    }
}
