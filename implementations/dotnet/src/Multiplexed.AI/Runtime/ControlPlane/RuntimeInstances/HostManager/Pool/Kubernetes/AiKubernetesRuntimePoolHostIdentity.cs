namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Represents the first-class identity of one created Kubernetes Runtime Pool Pod incarnation.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolHostIdentity
    {
        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the authoritative immutable host incarnation identifier.
        /// </summary>
        /// <remarks>
        /// For Kubernetes Runtime Pools this value is the Kubernetes Pod UID.
        /// </remarks>
        public required string HostId { get; init; }

        /// <summary>
        /// Gets the immutable Pod creation request identity.
        /// </summary>
        public required string PodRequestId { get; init; }

        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public required string Namespace { get; init; }

        /// <summary>
        /// Gets the Kubernetes Pod name.
        /// </summary>
        public required string PodName { get; init; }
    }
}
