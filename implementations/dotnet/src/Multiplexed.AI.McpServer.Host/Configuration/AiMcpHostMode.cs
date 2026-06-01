namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>
    /// Defines the MCP host execution mode.
    /// </summary>
    public enum AiMcpHostMode
    {
        /// <summary>
        /// Runs only the MCP control-plane server and shared queue pump.
        /// </summary>
        ControlPlaneOnly = 0,

        /// <summary>
        /// Runs the MCP control-plane server plus in-process local runtime instances for demo purposes.
        /// </summary>
        ControlPlaneWithLocalRuntimeInstances = 1,

        /// <summary>
        /// Runs only a runtime instance node without exposing MCP tools.
        /// </summary>
        RuntimeInstanceOnly = 2
    }
}