using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines Redis configuration options for runtime scale-out request persistence.
    /// </summary>
    /// <remarks>
    /// This options class extends the common scale-out request store options with
    /// Redis-specific storage settings.
    ///
    /// Common inherited options include:
    /// <list type="bullet">
    /// <item><description><see cref="AiRuntimeScaleOutRequestStoreOptions.DefaultTtl" />.</description></item>
    /// <item><description><see cref="AiRuntimeScaleOutRequestStoreOptions.DeduplicationWindow" />.</description></item>
    /// <item><description><see cref="AiRuntimeScaleOutRequestStoreOptions.MaxListResults" />.</description></item>
    /// <item><description><see cref="AiRuntimeScaleOutRequestStoreOptions.EnableDeduplication" />.</description></item>
    /// </list>
    /// </remarks>
    public sealed class RedisAiRuntimeScaleOutRequestStoreOptions : AiRuntimeScaleOutRequestStoreOptions
    {
        /// <summary>
        /// Gets or sets the Redis key prefix used by the scale-out request store.
        /// </summary>
        public string KeyPrefix { get; set; } = "ai";

        /// <summary>
        /// Gets or sets the Redis database index to use.
        /// </summary>
        /// <remarks>
        /// When <see langword="null" />, the default database configured on the Redis connection is used.
        /// </remarks>
        public int? Database { get; set; }

        /// <summary>
        /// Gets or sets the default index scan limit used by Redis list operations.
        /// </summary>
        /// <remarks>
        /// This limits how many Redis sorted set entries are inspected before query filtering is applied.
        /// The final returned result count is still controlled by
        /// <see cref="AiRuntimeScaleOutRequestStoreOptions.MaxListResults" />.
        /// </remarks>
        public int DefaultIndexScanLimit { get; set; } = 1_000;
    }
}