namespace Multiplexed.AI.Runtime.Invocation.Durable.Mongo
{
    /// <summary>
    /// Uses the host's existing IMongoDatabase and client lifetime. Required indexes and
    /// acknowledged writes cannot be disabled for this authoritative journal.
    /// </summary>
    public sealed class AiDurableInvocationMongoOptions
    {
        public string CollectionName { get; set; } = "ai_durable_invocations";
    }
}
