namespace Multiplexed.AI.Configuration
{
    /// <summary>
    /// Defines control-plane identity and discovery options for shared runtime orchestration.
    /// </summary>
    public sealed class AiControlPlaneOptions
    {
        /// <summary>
        /// Gets or sets the default control-plane identifier used when no explicit identifier has been resolved yet.
        /// </summary>
        public const string DefaultControlPlaneId = "default-control-plane";

        /// <summary>
        /// Gets or sets the default Redis discovery key used by runtime hosts to discover the active control-plane identifier.
        /// </summary>
        public const string DefaultRedisDiscoveryKey = "multiplexed-ai:default-control-plane";

        /// <summary>
        /// Gets or sets the logical control-plane identifier used to isolate shared runtime state.
        /// </summary>
        public string ControlPlaneId { get; set; } = DefaultControlPlaneId;

        /// <summary>
        /// Gets or sets the Redis discovery key used by runtime hosts to discover the active control-plane identifier.
        /// </summary>
        public string RedisDiscoveryKey { get; set; } = DefaultRedisDiscoveryKey;

        /// <summary>
        /// Gets or sets a value indicating whether this host publishes the control-plane discovery entry.
        /// </summary>
        public bool PublishDiscovery { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this host requires Redis discovery when no control-plane identifier is configured.
        /// </summary>
        public bool RequireDiscovery { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether Redis discovery is enabled.
        /// </summary>
        public bool EnableDiscovery { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the Redis discovery entry should be written with a TTL.
        /// </summary>
        public bool EnableDiscoveryTtl { get; set; } = true;

        /// <summary>
        /// Gets or sets the control-plane discovery entry time-to-live.
        /// </summary>
        public TimeSpan DiscoveryTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum amount of time to wait for discovery resolution.
        /// </summary>
        public TimeSpan DiscoveryResolutionTimeout { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Gets or sets the delay between discovery resolution attempts.
        /// </summary>
        public TimeSpan DiscoveryResolutionPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    }
}