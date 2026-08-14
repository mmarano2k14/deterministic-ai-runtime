namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Provides MongoDB configuration for the authoritative runtime-pool failure journal.
    /// </summary>
    public sealed class AiRuntimePoolFailureJournalMongoOptions
    {
        public string? ConnectionString { get; set; }

        public string? DatabaseName { get; set; }

        public string CollectionName { get; set; } = "ai_runtime_pool_failures";

        public bool EnsureIndexes { get; set; } = true;
    }
}
