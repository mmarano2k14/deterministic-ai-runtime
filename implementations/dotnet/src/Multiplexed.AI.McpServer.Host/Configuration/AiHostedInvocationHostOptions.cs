namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>
    /// Deployment-owned hosted invocation configuration. Exact executable and loader paths are supplied by the host;
    /// tenant publications can only select one of the resulting immutable environment references.
    /// </summary>
    public sealed class AiHostedInvocationHostOptions
    {
        public bool Enabled { get; set; }
        public int MaxConcurrentProcesses { get; set; } = 6;
        public int PollPageSize { get; set; } = 16;
        public int PollIntervalMilliseconds { get; set; } = 250;
        public string TenantId { get; set; } = "matrix-tenant";
        public string TenantGroupId { get; set; } = "matrix-group";
        public string ControlPlaneId { get; set; } = "matrix-control";
        public AiHostedRuntimeOptions DotNet { get; set; } = new();
        public AiHostedRuntimeOptions TypeScript { get; set; } = new();
        public AiHostedRuntimeOptions Python { get; set; } = new();
    }

    public sealed class AiHostedRuntimeOptions
    {
        public string Reference { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string WorkerPath { get; set; } = string.Empty;
        public string? WorkerDepsPath { get; set; }
        public string? WorkerRuntimeConfigPath { get; set; }
        public string? WorkingDirectory { get; set; }
    }
}
