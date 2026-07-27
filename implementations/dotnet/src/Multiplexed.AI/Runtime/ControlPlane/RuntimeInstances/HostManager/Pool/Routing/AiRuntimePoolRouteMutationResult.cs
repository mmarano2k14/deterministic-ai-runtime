namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents the result of a first-class route lifecycle mutation.
    /// </summary>
    public sealed record AiRuntimePoolRouteMutationResult
    {
        /// <summary>
        /// Gets the mutation status.
        /// </summary>
        public AiRuntimePoolRouteMutationStatus Status { get; init; }

        /// <summary>
        /// Gets the route snapshot after the mutation when the route still exists.
        /// </summary>
        public AiRuntimePoolRouteDescriptor? Route { get; init; }
    }
}
