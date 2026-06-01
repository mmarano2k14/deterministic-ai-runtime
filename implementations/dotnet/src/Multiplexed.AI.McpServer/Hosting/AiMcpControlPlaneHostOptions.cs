namespace Multiplexed.AI.McpServer.Hosting
{
    /// <summary>
    /// Defines options for the MCP control-plane host.
    /// </summary>
    public sealed class AiMcpControlPlaneHostOptions
    {
        /// <summary>
        /// Gets or sets whether the MCP control-plane background services are enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the shared queue background pump should be enabled.
        /// </summary>
        public bool EnableSharedQueuePump { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the MCP host should use remote shared run dispatching.
        /// </summary>
        public bool UseRemoteDispatcher { get; set; } = true;

        /// <summary>
        /// Gets or sets the MCP control-plane runtime instance identifier.
        /// </summary>
        public string RuntimeInstanceId { get; set; } = "mcp-control-plane";

        /// <summary>
        /// Gets or sets the MCP control-plane worker identifier.
        /// </summary>
        public string WorkerId { get; set; } = "mcp-background-pump";
    }
}