namespace Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement
{
    /// <summary>
    /// Represents typed hierarchical placement identities for run admission.
    /// </summary>
    /// <remarks>
    /// RuntimeInstanceId is enforced by the current admission controller.
    /// HostId, PoolId, and NodeId are first-class extension points for later
    /// hierarchical capacity selection without encoding correctness data in metadata.
    /// </remarks>
    public sealed class AiRunPlacementTarget
    {
        /// <summary>
        /// Optional exact runtime instance identity.
        /// </summary>
        public string? RuntimeInstanceId { get; init; }

        /// <summary>
        /// Optional runtime host or Pod identity.
        /// </summary>
        public string? HostId { get; init; }

        /// <summary>
        /// Optional runtime pool identity.
        /// </summary>
        public string? PoolId { get; init; }

        /// <summary>
        /// Optional provider-neutral node identity.
        /// A Kubernetes provider may resolve this to a Kubernetes node name.
        /// </summary>
        public string? NodeId { get; init; }
    }
}
