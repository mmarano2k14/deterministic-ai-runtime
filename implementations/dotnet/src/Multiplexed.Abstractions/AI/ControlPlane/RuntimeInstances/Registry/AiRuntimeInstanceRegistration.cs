namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Represents a runtime instance registration request.
    /// </summary>
    /// <remarks>
    /// A runtime instance represents one independently addressable runtime process.
    /// The existing Kubernetes host creation mode normally maps one runtime instance to one pod,
    /// while a runtime pool may host several runtime instances under the same host incarnation.
    /// Correctness-critical identity must be represented by first-class properties and must not
    /// be inferred from optional metadata.
    /// </remarks>
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
        /// Gets the tenant identifier that owns this runtime instance, when tenant-scoped.
        /// </summary>
        /// <remarks>
        /// This value is a first-class routing and isolation field.
        /// Metadata may duplicate it for diagnostics, but tenant-aware registry filtering
        /// must not depend only on metadata.
        /// </remarks>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets the tenant group identifier that owns this runtime instance, when group-scoped.
        /// </summary>
        /// <remarks>
        /// This value is a first-class routing and isolation field.
        /// It allows dedicated or hybrid group-owned runtime instances to be matched without
        /// relying only on metadata.
        /// </remarks>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the immutable identity of the exact host incarnation that owns this runtime instance.
        /// </summary>
        /// <remarks>
        /// Multiple runtime instances may share this value when they are hosted by the same runtime
        /// pool host. Providers map their exact host-incarnation identity to this generic field; for
        /// example, a Kubernetes runtime pool maps the Kubernetes pod UID to <see cref="HostId"/>.
        /// Reusable names such as pod names, service names, or machine names are not authoritative
        /// host identities.
        /// </remarks>
        public string? HostId { get; init; }

        /// <summary>
        /// Gets the logical runtime pool identifier that owns this runtime instance.
        /// </summary>
        /// <remarks>
        /// This is a first-class membership and placement identity. Metadata may duplicate it for
        /// diagnostics, but runtime pool membership must not depend on metadata.
        /// </remarks>
        public string? PoolId { get; init; }

        /// <summary>
        /// Gets the logical runtime identity inside the owning host.
        /// </summary>
        public string? RuntimeId { get; init; }

        /// <summary>
        /// Gets the control-plane host identity that owns or manages this runtime instance.
        /// </summary>
        public string? ControlPlaneHostId { get; init; }

        /// <summary>
        /// Gets the logical control-plane identifier that owns this capacity descriptor.
        /// </summary>
        public string? ControlPlaneId { get; init; }

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
        /// Gets optional non-authoritative metadata for diagnostics, observability, provider labels,
        /// dashboards, zones, or deployment information.
        /// </summary>
        /// <remarks>
        /// Metadata must not control routing, membership, lifecycle, draining, capacity selection,
        /// or recovery. Any value required for correctness must be represented by a typed property.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();

        public DateTimeOffset RegisteredAtUtc { get; init; }
    }
}