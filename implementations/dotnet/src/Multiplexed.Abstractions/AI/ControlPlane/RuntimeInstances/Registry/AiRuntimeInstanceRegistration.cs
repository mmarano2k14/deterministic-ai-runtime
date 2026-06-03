namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Represents a runtime instance registration request.
    ///
    /// A runtime instance corresponds to one runtime process.
    /// In Kubernetes, one runtime instance normally corresponds to one pod/replica.
    /// </summary>
    public sealed class AiRuntimeInstanceRegistration
    {

        /// <summary>
        /// Optional MCP runtime identifier that owns this runtime instance.
        /// Multiple runtime instances may belong to the same MCP runtime.
        /// </summary>
        public string? McpRuntimeId { get; init; }

        /// <summary>
        /// Defines the logical role of the runtime registration.
        /// </summary>
        public AiRuntimeInstanceRole Role { get; set; }
            = AiRuntimeInstanceRole.Runtime;

        /// <summary>
        /// Runtime process / Kubernetes pod / replica identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Optional host name where the runtime instance is running.
        /// </summary>
        public string? HostName { get; init; }

        /// <summary>
        /// Optional process id for local diagnostics.
        /// </summary>
        public int? ProcessId { get; init; }

        /// <summary>
        /// Optional Kubernetes namespace when running inside Kubernetes.
        /// </summary>
        public string? KubernetesNamespace { get; init; }

        /// <summary>
        /// Optional Kubernetes pod name when running inside Kubernetes.
        /// </summary>
        public string? KubernetesPodName { get; init; }

        /// <summary>
        /// Optional Kubernetes node name when running inside Kubernetes.
        /// </summary>
        public string? KubernetesNodeName { get; init; }

        /// <summary>
        /// Number of local workers owned by this runtime instance.
        /// </summary>
        public int WorkerCount { get; init; }

        /// <summary>
        /// Maximum number of local runs that can execute concurrently on this runtime instance.
        /// </summary>
        public int? MaxConcurrentRuns { get; init; }

        /// <summary>
        /// Maximum local queue capacity for this runtime instance.
        /// </summary>
        public int? QueueCapacity { get; init; }

        /// <summary>
        /// Optional runtime version, package version, or build version.
        /// Useful for rolling upgrade diagnostics.
        /// </summary>
        public string? RuntimeVersion { get; init; }

        /// <summary>
        /// Optional metadata for future dashboard, Kubernetes, tenant, zone, or deployment labels.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}