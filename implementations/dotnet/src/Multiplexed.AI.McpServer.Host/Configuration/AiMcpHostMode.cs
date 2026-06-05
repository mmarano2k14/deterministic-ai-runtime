namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>
    /// Defines the MCP host runtime mode.
    /// </summary>
    public enum AiMcpHostMode
    {
        /// <summary>
        /// Runs only the MCP control-plane.
        /// </summary>
        ControlPlaneOnly = 0,

        /// <summary>
        /// Runs the MCP control-plane with local runtime instances in the same process.
        /// </summary>
        ControlPlaneWithLocalRuntimeInstances = 1,

        /// <summary>
        /// Runs only a runtime instance without MCP control-plane tools.
        /// </summary>
        RuntimeInstanceOnly = 2,

        /// <summary>
        /// Runs the MCP control-plane and dispatches to HTTP-addressable runtime instances.
        /// </summary>
        ControlPlaneWithHttpRuntimeInstances = 3
    }
}