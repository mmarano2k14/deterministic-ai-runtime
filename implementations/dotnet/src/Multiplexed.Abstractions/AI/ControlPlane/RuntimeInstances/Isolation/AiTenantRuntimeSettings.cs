namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation
{
    /// <summary>
    /// Describes runtime capacity and isolation settings for a tenant.
    /// </summary>
    public sealed class AiTenantRuntimeSettings
    {
        /// <summary>
        /// Gets the tenant identifier these settings apply to.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the optional tenant group identifier.
        /// </summary>
        public string? TenantGroupId { get; init; }

        /// <summary>
        /// Gets the runtime instance isolation mode for this tenant.
        /// </summary>
        public AiRuntimeInstanceIsolationMode IsolationMode { get; init; } =
            AiRuntimeInstanceIsolationMode.Shared;

        /// <summary>
        /// Gets whether dedicated capacity should be preferred when available.
        /// </summary>
        public bool PreferDedicatedCapacity { get; init; }

        /// <summary>
        /// Gets whether this tenant may fallback to shared runtime capacity.
        /// </summary>
        public bool AllowSharedFallback { get; init; } = true;

        /// <summary>
        /// Gets the maximum number of runtime instances allowed for this tenant.
        /// </summary>
        public int MaxRuntimeInstances { get; init; } = 1;

        /// <summary>
        /// Gets the number of workers per runtime instance.
        /// </summary>
        public int WorkerCountPerInstance { get; init; } = 10;

        /// <summary>
        /// Gets the maximum number of concurrent runs per runtime instance.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; init; } = 3;

        /// <summary>
        /// Gets the optional local queue capacity for runtime instances.
        /// </summary>
        public int? LocalQueueCapacity { get; init; }

        /// <summary>
        /// Gets the runtime instance identifier prefix to use for this tenant.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; init; } = "runtime-instance";

        /// <summary>
        /// Gets provider-specific metadata or future policy metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>();
    }
}