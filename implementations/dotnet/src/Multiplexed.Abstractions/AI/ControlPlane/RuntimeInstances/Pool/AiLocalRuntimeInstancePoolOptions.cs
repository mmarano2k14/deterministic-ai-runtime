namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Pool
{
    /// <summary>
    /// Defines local runtime instance pool settings.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Simulates multiple runtime instances on a single host.
    /// - Enables local benchmarking before Kubernetes deployment.
    /// - Provides a bridge between single-host and multi-host execution.
    ///
    /// IMPORTANT:
    /// - Each runtime instance owns its own local queue.
    /// - Each runtime instance owns its own workers.
    /// - Shared queue dispatch remains global.
    /// </remarks>
    public sealed class AiLocalRuntimeInstancePoolOptions
    {
        /// <summary>
        /// Enables the local runtime instance pool.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Number of runtime instances to create.
        /// </summary>
        public int InstanceCount { get; set; } = 1;

        /// <summary>
        /// Number of workers created per runtime instance.
        /// </summary>
        public int WorkerCountPerInstance { get; set; } = 10;

        /// <summary>
        /// Maximum number of concurrent runs accepted by a runtime instance.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; set; } = 3;

        /// <summary>
        /// Maximum local queue capacity.
        /// Null means unlimited.
        /// </summary>
        public int? LocalQueueCapacity { get; set; }

        /// <summary>
        /// Prefix used when generating runtime instance identifiers.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } =
            "runtime-instance";

        /// <summary>
        /// Metadata copied to each local runtime instance created by the pool.
        /// </summary>
        /// <remarks>
        /// This is used to propagate runtime instance ownership and isolation
        /// information, such as tenant id, tenant group id, or runtime isolation mode,
        /// into the runtime instance registration and capacity descriptors.
        ///
        /// When empty, local runtime instances remain shared/backward-compatible.
        /// </remarks>
        public Dictionary<string, string> Metadata { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}