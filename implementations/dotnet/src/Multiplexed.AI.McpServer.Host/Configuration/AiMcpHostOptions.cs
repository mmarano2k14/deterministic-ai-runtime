namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>
    /// Represents MCP host configuration options.
    /// </summary>
    public sealed class AiMcpHostOptions
    {
        /// <summary>
        /// Gets or sets the MCP host execution mode.
        /// </summary>
        public AiMcpHostMode Mode { get; set; } =
            AiMcpHostMode.ControlPlaneOnly;

        /// <summary>
        /// Gets or sets the HTTP port.
        /// </summary>
        public int Port { get; set; } = 8080;

        /// <summary>
        /// Gets or sets a value indicating whether the shared queue pump is enabled.
        /// </summary>
        public bool EnableSharedQueuePump { get; set; } = true;

        /// <summary>
        /// Gets or sets the shared queue pump interval in seconds.
        /// </summary>
        public int SharedQueuePumpIntervalSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets a value indicating whether replay tools are enabled.
        /// </summary>
        public bool EnableReplayTools { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether observability tools are enabled.
        /// </summary>
        public bool EnableObservabilityTools { get; set; } = true;
    }
}