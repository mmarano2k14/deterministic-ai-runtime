namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Configures the application pipeline.
    /// </summary>
    public static class ApplicationConfiguration
    {
        /// <summary>
        /// Configures the application.
        /// </summary>
        /// <param name="app">The web application.</param>
        public static void Configure(
            WebApplication app)
        {
            ArgumentNullException.ThrowIfNull(app);

            app.MapHealthChecks("/health");
            app.MapHealthChecks("/ready");

            app.MapMcp("/mcp");
        }
    }
}