namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Store
{
    /// <summary>
    /// Defines Redis storage options for shared runtime controller run records.
    /// </summary>
    public sealed class RedisAiSharedRunStoreOptions
    {
        /// <summary>
        /// Gets the Redis key prefix used by the shared run store.
        /// </summary>
        public string KeyPrefix { get; set; } = "ai:shared-runs";

        /// <summary>
        /// Gets the Redis sorted set key used to index shared runs by submission time.
        /// </summary>
        public string IndexKey => $"{KeyPrefix}:index";

        /// <summary>
        /// Gets the maximum number of shared run ids scanned when listing runs.
        /// </summary>
        public int ListScanLimit { get; init; } = 500;

        /// <summary>
        /// Indicates whether shared run records should expire automatically.
        /// </summary>
        public bool EnableRecordExpiration { get; init; }

        /// <summary>
        /// Optional shared run record expiration.
        /// </summary>
        public TimeSpan? RecordExpiration { get; init; }

        /// <summary>
        /// Builds the Redis hash key for a shared run.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The Redis hash key.</returns>
        public string BuildRunKey(
            string sharedRunId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            return $"{KeyPrefix}:run:{sharedRunId}";
        }
    }
}