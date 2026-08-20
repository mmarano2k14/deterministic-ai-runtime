using Multiplexed.Abstractions.AI.ControlPlane.Redis;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue.Redis
{
    /// <summary>
    /// Provides Redis storage options for the runtime run execution index.
    /// </summary>
    /// <remarks>
    /// The runtime run execution index stores the durable relationship between
    /// a local runtime RunId and the DAG ExecutionId created from that run.
    ///
    /// This index is used by the control plane after a local runtime queue item
    /// has already been consumed by the background controller.
    ///
    /// Multi-tenant isolation is enforced through ExecutionContextSnapshot.TenantId.
    /// ExecutionContextSnapshot.ContextKey is volatile and must not be used as a
    /// durable Redis partition key.
    /// </remarks>
    public sealed class RedisAiRuntimeRunExecutionIndexOptions
    {
        /// <summary>
        /// Gets or sets the Redis key prefix.
        /// </summary>
        /// <remarks>
        /// The default prefix produces keys such as:
        /// ai:control-plane:{controlPlaneId}:runtime-run-index:item:{runId}
        /// </remarks>
        public string KeyPrefix { get; set; } = AiRedisControlPlaneDefaults.DefaultKeyPrefix;

        /// <summary>
        /// Gets or sets a value indicating whether Redis records should expire automatically.
        /// </summary>
        public bool EnableRecordExpiration { get; set; } = true;

        /// <summary>
        /// Gets or sets the Redis expiration applied to runtime run index records.
        /// </summary>
        /// <remarks>
        /// This should usually be longer than the local runtime queue lifecycle,
        /// because the index is used after the local queue item may already be gone.
        /// </remarks>
        public TimeSpan? RecordExpiration { get; set; } = TimeSpan.FromHours(6);
    }
}
