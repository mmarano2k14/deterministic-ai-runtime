namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Requests durable assigned-work enumeration for one exactly suppressed Pod incarnation.
    /// </summary>
    public sealed record AiKubernetesRuntimePoolPodAssignedWorkRequest
    {
        /// <summary>
        /// Gets the immutable Pod failure identifier used by every child suppression.
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
