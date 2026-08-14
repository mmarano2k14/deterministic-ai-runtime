namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Requests atomic capacity suppression for one exact Kubernetes Runtime Pool Pod incarnation.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodCapacitySuppressionRequest
    {
        /// <summary>
        /// Gets the immutable failure observation identifier.
        /// </summary>
        public required string FailureId { get; init; }

        /// <summary>
        /// Gets the logical Runtime Pool identifier.
        /// </summary>
        public required string PoolId { get; init; }

        /// <summary>
        /// Gets the immutable Kubernetes Pod UID.
        /// </summary>
        public required string PodUid { get; init; }

        /// <summary>
        /// Gets the authoritative host incarnation identifier.
        /// </summary>
        public string HostId => this.PodUid;
    }
}
