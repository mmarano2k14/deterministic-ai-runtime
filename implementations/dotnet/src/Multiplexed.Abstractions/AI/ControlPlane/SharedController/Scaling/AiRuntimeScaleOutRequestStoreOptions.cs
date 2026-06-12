namespace Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Defines common configuration options for runtime scale-out request stores.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestStoreOptions
    {
        /// <summary>
        /// Gets or sets the default time-to-live applied to scale-out request records.
        /// </summary>
        public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Gets or sets the default deduplication window used to prevent request floods.
        /// </summary>
        public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum number of scale-out requests returned by list operations.
        /// </summary>
        public int MaxListResults { get; set; } = 500;

        /// <summary>
        /// Gets or sets a value indicating whether duplicate pending requests should be suppressed.
        /// </summary>
        public bool EnableDeduplication { get; set; } = true;
    }
}