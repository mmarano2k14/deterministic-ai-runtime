namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Configures host-level settings for the MCP control-plane host.
    /// </summary>
    public static class HostConfiguration
    {
        /// <summary>
        /// Configures the web application host.
        /// </summary>
        /// <param name="builder">The web application builder.</param>
        public static void Configure(
            WebApplicationBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            object value = builder.Host.UseWindowsService();
        }
    }
}