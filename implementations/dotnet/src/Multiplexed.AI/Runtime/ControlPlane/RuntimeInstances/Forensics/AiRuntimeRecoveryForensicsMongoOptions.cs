namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Provides MongoDB configuration for runtime recovery forensics persistence.
    /// </summary>
    public sealed class AiRuntimeRecoveryForensicsMongoOptions
    {
        /// <summary>
        /// Gets or sets the MongoDB connection string.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the MongoDB database name.
        /// </summary>
        public string? DatabaseName { get; set; }

        /// <summary>
        /// Gets or sets the MongoDB collection name.
        /// </summary>
        public string CollectionName { get; set; } = "ai_runtime_recovery_forensics";

        /// <summary>
        /// Gets or sets a value indicating whether indexes should be ensured during store startup.
        /// </summary>
        public bool EnsureIndexes { get; set; } = true;
    }
}