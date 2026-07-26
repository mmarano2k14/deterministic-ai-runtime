namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Defines registration and heartbeat settings for a runtime instance.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Controls how a runtime instance registers itself in the runtime instance registry.
    /// - Provides visibility information used by MCP, HTTP APIs, dashboards,
    ///   shared admission, autoscaling, and diagnostics.
    /// - Supplies capacity information such as worker count, queue limits,
    ///   and concurrent execution limits.
    ///
    /// IMPORTANT:
    /// - This type is provider-neutral.
    /// - Kubernetes, Docker, local process, systemd, or cloud-specific metadata
    ///   must be supplied through provider metadata, not hardcoded properties.
    /// - These settings do not control worker execution behavior directly.
    /// - These settings only affect runtime instance visibility and registration.
    /// </remarks>
    public sealed class AiRuntimeInstanceRegistrationOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether runtime instance registration is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the runtime instance identifier.
        /// </summary>
        /// <remarks>
        /// When not provided, the runtime may generate or resolve an identifier automatically
        /// from the configured runtime environment provider.
        /// </remarks>
        public string? RuntimeInstanceId { get; set; }

        /// <summary>
        /// Gets or sets the optional logical runtime pool identifier.
        /// </summary>
        /// <remarks>
        /// This is an authoritative first-class membership identity. Registration, capacity,
        /// lifecycle, routing, and recovery must not infer it from metadata.
        /// </remarks>
        public string? PoolId { get; set; }

        /// <summary>
        /// Gets or sets the optional immutable identifier of the exact host incarnation that
        /// contains this runtime instance.
        /// </summary>
        /// <remarks>
        /// Several independent runtime instances may share this value when they are hosted by one
        /// process pool manager or one future Kubernetes pool pod. Provider-specific identities are
        /// mapped to this generic value at the provider boundary.
        /// </remarks>
        public string? HostId { get; set; }

        /// <summary>
        /// Gets or sets the runtime version exposed to the registry.
        /// </summary>
        public string? RuntimeVersion { get; set; }

        /// <summary>
        /// Gets or sets the number of local workers owned by this runtime instance.
        /// </summary>
        public int WorkerCount { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum local queue capacity.
        /// </summary>
        public int? QueueCapacity { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of runs that can execute concurrently.
        /// </summary>
        public int? MaxConcurrentRuns { get; set; }

        /// <summary>
        /// Gets or sets the interval between runtime instance heartbeats.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } =
            TimeSpan.FromSeconds(5);

        /// <summary>
        /// Gets or sets the time-to-live applied to the runtime instance registry descriptor.
        /// </summary>
        /// <remarks>
        /// The TTL protects Redis-backed registries from stale runtime instances when a host,
        /// process, test, or Kubernetes pod stops unexpectedly without unregistering cleanly.
        /// The heartbeat loop must renew this TTL periodically.
        /// </remarks>
        public TimeSpan RegistryTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the time-to-live applied to the runtime instance capacity descriptor.
        /// </summary>
        /// <remarks>
        /// The TTL protects Redis-backed capacity stores from stale capacity descriptors when a host,
        /// process, test, or Kubernetes pod stops unexpectedly without deleting its capacity state.
        /// The heartbeat or capacity publication loop must renew this TTL periodically.
        /// </remarks>
        public TimeSpan CapacityTtl { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the runtime environment provider name.
        /// </summary>
        /// <remarks>
        /// Example values:
        /// - local
        /// - docker
        /// - kubernetes
        /// - systemd
        /// - nomad
        /// </remarks>
        public string? ProviderName { get; set; }

        /// <summary>
        /// Gets or sets provider-specific metadata.
        /// </summary>
        /// <remarks>
        /// Examples:
        /// - Kubernetes provider: namespace, pod, node, deployment.
        /// - Docker provider: container id, image, host.
        /// - Local provider: machine name, process path.
        /// </remarks>
        public IReadOnlyDictionary<string, string> ProviderMetadata { get; set; } =
            new Dictionary<string, string>();

        /// <summary>
        /// Gets or sets optional runtime metadata.
        /// </summary>
        /// <remarks>
        /// This metadata is provider-neutral and can be used for environment,
        /// tenant, deployment, region, zone, or dashboard labels.
        /// </remarks>
        public IReadOnlyDictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>();

        public AiRuntimeInstanceRole Role { get; set; } = AiRuntimeInstanceRole.Runtime;

        
    }
}