namespace Multiplexed.Abstractions.AI.ControlPlane.Discovery
{
    /// <summary>
    /// Describes the currently published control-plane identity used by runtime hosts
    /// to discover the shared control-plane scope they must join.
    /// </summary>
    public sealed class AiControlPlaneDiscoveryDescriptor
    {
        /// <summary>
        /// Gets or sets the Redis discovery key used as the rendezvous key between
        /// the control-plane host and runtime hosts.
        /// </summary>
        public string RedisDiscoveryKey { get; set; } = "multiplexed-ai:default-control-plane";

        /// <summary>
        /// Gets or sets the logical control-plane identifier used to isolate shared runtime state.
        /// </summary>
        public string ControlPlaneId { get; set; } = "default-control-plane";

        /// <summary>
        /// Gets or sets the host identifier of the process or pod that published the descriptor.
        /// </summary>
        public string HostId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the runtime instance identifier of the publisher when available.
        /// </summary>
        public string RuntimeInstanceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the provider name associated with the publishing host.
        /// </summary>
        public string ProviderName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the UTC timestamp at which the descriptor was created.
        /// </summary>
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets or sets the UTC timestamp at which the descriptor was last refreshed.
        /// </summary>
        public DateTimeOffset HeartbeatAtUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}