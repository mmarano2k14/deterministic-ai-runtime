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
        public bool EnableWorkerPolling { get; set; } = true;
        public bool EnableDagReconciliation { get; set; } = true;
        public bool EnableLocalWorkerProfiles { get; set; } = true;
        public string TenantId { get; set; } = "matrix-tenant";
        public string TenantGroupId { get; set; } = "matrix-group";
        public string ControlPlaneId { get; set; } = "matrix-control";
        public AiHostedRuntimeOptions DotNet { get; set; } = new();
        public AiHostedRuntimeOptions TypeScript { get; set; } = new();
        public AiHostedRuntimeOptions Python { get; set; } = new();
        public List<AiHostedPublicationOnlyRuntimeOptions> PublicationOnlyRuntimes { get; set; } = new();
        public AiHostedContainerWorkersOptions Container { get; set; } = new();
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

    /// <summary>
    /// One exact runtime identity available for publication on another runtime host. The current host
    /// advertises this environment but does not install a local worker executable for it.
    /// </summary>
    public sealed class AiHostedPublicationOnlyRuntimeOptions
    {
        public string Reference { get; set; } = string.Empty;
        public string ExecutionLanguage { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string RuntimeSha256 { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = "linux";
        public string Architecture { get; set; } = "amd64";
    }

    /// <summary>
    /// Optional deployment-owned OCI provider configuration. The engine path, owner scope,
    /// image repositories and immutable manifest digests are host configuration rather than tenant input.
    /// </summary>
    public sealed class AiHostedContainerWorkersOptions
    {
        public bool Enabled { get; set; }
        public string EngineExecutablePath { get; set; } = string.Empty;
        public string? EngineWorkingDirectory { get; set; }
        public string ContainerOwnerScope { get; set; } = string.Empty;
        public int CpuMilliCores { get; set; } = 1000;
        public long MemoryBytes { get; set; } = 268435456;
        public int PidsLimit { get; set; } = 64;
        public long WritableWorkspaceBytes { get; set; } = 67108864;
        public int HeartbeatMilliseconds { get; set; } = 1000;
        public Dictionary<string, string> EngineEnvironment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AiHostedContainerRuntimeOptions> Runtimes { get; set; } = new();
    }

    /// <summary>
    /// One exact host-approved OCI worker runtime. Mutable tags are not accepted; ImageDigest
    /// identifies the platform-specific manifest selected for the configured language runtime.
    /// </summary>
    public sealed class AiHostedContainerRuntimeOptions
    {
        public string Reference { get; set; } = string.Empty;
        public string ExecutionLanguage { get; set; } = string.Empty;
        public string RuntimeVersion { get; set; } = string.Empty;
        public string ImageRepository { get; set; } = string.Empty;
        public string ImageDigest { get; set; } = string.Empty;
        public string ContainerUser { get; set; } = "65532:65532";
    }
}
