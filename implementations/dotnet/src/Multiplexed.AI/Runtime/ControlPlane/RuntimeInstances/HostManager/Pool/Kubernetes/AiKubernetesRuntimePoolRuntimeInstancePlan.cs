namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Represents one independently identifiable runtime planned inside a Kubernetes Runtime Pool Pod.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolRuntimeInstancePlan
    {
        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable request identity that created the containing Pod plan.
        /// </summary>
        public required string PodRequestId { get; init; }

        /// <summary>
        /// Gets the one-based runtime ordinal used for topology and diagnostics.
        /// </summary>
        /// <remarks>
        /// Correctness relies on <see cref="RuntimeInstanceId"/>, not on this ordinal.
        /// </remarks>
        public int Ordinal { get; init; }

        /// <summary>
        /// Gets the globally unique runtime instance identifier.
        /// </summary>
        public required string RuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the runtime provider name.
        /// </summary>
        public required string ProviderName { get; init; }

        /// <summary>
        /// Gets the command transport name.
        /// </summary>
        public required string TransportName { get; init; }

        /// <summary>
        /// Gets the internal child transport port within the Pod.
        /// </summary>
        public int TransportPort { get; init; }
    }
}
