using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Validates MCP host configuration during startup.
    /// </summary>
    public static class HostModeValidator
    {
        /// <summary>
        /// Validates MCP host options.
        /// </summary>
        /// <param name="options">The MCP host options.</param>
        public static void Validate(
            AiMcpHostOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.Port <= 0)
            {
                throw new InvalidOperationException(
                    "The MCP host port must be greater than zero.");
            }
        }
    }
}