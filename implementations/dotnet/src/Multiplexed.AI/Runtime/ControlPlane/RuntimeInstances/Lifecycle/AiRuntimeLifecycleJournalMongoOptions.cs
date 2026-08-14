namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Provides MongoDB configuration for runtime lifecycle journal persistence.
    /// </summary>
    public sealed class AiRuntimeLifecycleJournalMongoOptions
    {
        /// <summary>
        /// Gets or sets the MongoDB connection string when the journal owns registration.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the MongoDB database name when the journal owns registration.
        /// </summary>
        public string? DatabaseName { get; set; }

        /// <summary>
        /// Gets or sets the MongoDB collection name.
        /// </summary>
        public string CollectionName { get; set; } = "ai_runtime_lifecycle_events";

        /// <summary>
        /// Gets or sets a value indicating whether query indexes should be ensured lazily.
        /// </summary>
        public bool EnsureIndexes { get; set; } = true;
    }
}
