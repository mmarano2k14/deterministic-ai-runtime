namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Provides configuration for the process-host Runtime Pool Manager.
    /// </summary>
    /// <remarks>
    /// These options configure the new opt-in process pool lifecycle. They do not change the
    /// existing <c>Process</c> host creation strategy or the existing Kubernetes host creation
    /// mode.
    /// </remarks>
    public sealed class AiRuntimeProcessPoolOptions
    {
        /// <summary>
        /// Gets or sets a value indicating whether the process-host Runtime Pool Manager is enabled.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the logical identifier shared by every runtime instance in this pool.
        /// </summary>
        public string PoolId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the prefix used when generating the immutable host-incarnation identifier.
        /// </summary>
        /// <remarks>
        /// The exact <c>HostId</c> is generated at manager startup and must not be reused after a
        /// manager restart.
        /// </remarks>
        public string HostIdPrefix { get; set; } = "runtime-pool-host";

        /// <summary>
        /// Gets or sets the prefix used when generating independent runtime instance identifiers.
        /// </summary>
        public string RuntimeInstanceIdPrefix { get; set; } = "runtime-pool";

        /// <summary>
        /// Gets or sets the number of runtime processes created when the pool starts.
        /// </summary>
        public int InitialProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the minimum number of healthy runtime processes maintained by the pool.
        /// </summary>
        public int MinimumProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of runtime processes allowed in the pool host.
        /// </summary>
        public int MaximumProcessCount { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of child process startups that may run concurrently.
        /// </summary>
        public int StartupParallelism { get; set; } = 1;

        /// <summary>
        /// Gets or sets the graceful shutdown timeout in seconds.
        /// </summary>
        public int ShutdownTimeoutSeconds { get; set; } = 30;
    }
}
