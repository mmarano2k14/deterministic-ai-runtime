namespace Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Redis
{
    /// <summary>
    /// Defines Redis storage options for the shared/global queue.
    /// </summary>
    public sealed class RedisAiSharedQueueOptions
    {
        /// <summary>
        /// Gets the Redis key prefix used by the shared queue.
        /// </summary>
        public string KeyPrefix { get; set; } = "ai:shared-queue";

        /// <summary>
        /// Gets the Redis sorted set key containing pending shared run ids.
        /// </summary>
        public string PendingIndexKey => $"{KeyPrefix}:pending";

        /// <summary>
        /// Gets the Redis sorted set key containing all shared queue item ids.
        /// </summary>
        public string AllIndexKey => $"{KeyPrefix}:all";

        /// <summary>
        /// Gets the maximum number of queue items scanned when listing.
        /// </summary>
        public int ListScanLimit { get; init; } = 500;

        /// <summary>
        /// Indicates whether queue item records should expire automatically.
        /// </summary>
        public bool EnableRecordExpiration { get; init; }

        /// <summary>
        /// Optional queue item record expiration.
        /// </summary>
        public TimeSpan? RecordExpiration { get; init; }

        /// <summary>
        /// Builds the Redis hash key for a shared queue item.
        /// </summary>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <returns>The Redis hash key.</returns>
        public string BuildItemKey(
            string sharedRunId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);

            return $"{KeyPrefix}:item:{sharedRunId}";
        }
    }
}