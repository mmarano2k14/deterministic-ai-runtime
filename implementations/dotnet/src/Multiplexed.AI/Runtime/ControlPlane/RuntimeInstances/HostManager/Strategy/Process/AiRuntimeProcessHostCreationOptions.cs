namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process
{
    /// <summary>
    /// Provides configuration for process-based runtime host creation.
    /// </summary>
    public sealed class AiRuntimeProcessHostCreationOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether process-based runtime host creation is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the dotnet executable path used to start the runtime host assembly.
        /// </summary>
        public string DotnetExecutablePath { get; set; } = "dotnet";

        /// <summary>
        /// Gets or sets the runtime host assembly path.
        /// </summary>
        /// <remarks>
        /// This should point to the real Multiplexed.AI.McpServer.Host.dll assembly, not to a test assembly.
        /// </remarks>
        public string RuntimeHostAssemblyPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the working directory used when starting the runtime host process.
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Gets or sets the first TCP port that may be allocated to a runtime host process.
        /// </summary>
        public int BasePort { get; set; } = 5800;

        /// <summary>
        /// Gets or sets the last TCP port that may be allocated to a runtime host process.
        /// </summary>
        public int MaxPort { get; set; } = 5899;

        /// <summary>
        /// Gets or sets the process startup timeout in seconds.
        /// </summary>
        public int StartupTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets a value indicating whether process standard output and error streams should be redirected.
        /// </summary>
        public bool RedirectOutput { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether started runtime host processes should be killed when the strategy is disposed.
        /// </summary>
        public bool KillOnDispose { get; set; } = true;

        /// <summary>
        /// Gets or sets additional environment variables passed to the runtime host process.
        /// </summary>
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}