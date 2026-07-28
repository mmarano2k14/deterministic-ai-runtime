namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Provides strongly typed configuration for one opt-in Kubernetes Runtime Pool Pod.
    /// </summary>
    /// <remarks>
    /// These options describe the pool topology only. They do not modify the existing
    /// one-runtime-per-Pod Kubernetes host creation strategy.
    /// </remarks>
    public sealed class AiKubernetesRuntimePoolOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether Kubernetes Runtime Pool hosting is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the logical identifier shared by every runtime instance in the pool.
        /// </summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Kubernetes namespace in which the pool Pod will be created.
        /// </summary>
        public string Namespace { get; set; } = "default";

        /// <summary>
        /// Gets or sets the prefix used to build the Kubernetes Pod name.
        /// </summary>
        public string PodNamePrefix { get; set; } = "runtime-pool";

        /// <summary>
        /// Gets or sets the prefix used to generate independent runtime instance identifiers.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } = "runtime-pool";

        /// <summary>
        /// Gets or sets the runtime provider name used by every child in this pool Pod.
        /// </summary>
        public string ProviderName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the command transport name used by every child in this pool Pod.
        /// </summary>
        public string TransportName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the number of runtime instances created when the pool Pod starts.
        /// </summary>
        public int InitialRuntimeInstanceCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the minimum number of healthy runtime instances maintained in the Pod.
        /// </summary>
        public int MinimumRuntimeInstanceCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of runtime instances allowed in the Pod.
        /// </summary>
        public int MaximumRuntimeInstanceCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of child startups allowed to run concurrently.
        /// </summary>
        public int StartupParallelism { get; set; } = 1;

        /// <summary>
        /// Gets or sets the stable pool transport port exposed by the Pod.
        /// </summary>
        public int StableTransportPort { get; set; } = 8080;

        /// <summary>
        /// Gets or sets the dedicated HTTP/1 Kubernetes readiness port.
        /// </summary>
        /// <remarks>
        /// A clear-text gRPC endpoint must be HTTP/2-only. Readiness therefore uses a
        /// separate HTTP/1 endpoint instead of sharing the stable gRPC transport port.
        /// </remarks>
        public int ReadinessPort { get; set; } = 8081;

        /// <summary>
        /// Gets or sets the first internal child transport port.
        /// </summary>
        public int FirstChildTransportPort { get; set; } = 18080;

        /// <summary>
        /// Gets or sets the increment applied between consecutive child transport ports.
        /// </summary>
        public int ChildTransportPortStride { get; set; } = 1;

        /// <summary>
        /// Gets or sets the graceful shutdown timeout in seconds.
        /// </summary>
        public int ShutdownTimeoutSeconds { get; set; } = 30;
    }
}
