namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents the result of resolving an exact runtime route.
    /// </summary>
    public sealed record AiRuntimePoolRouteResolutionResult
    {
        /// <summary>
        /// Gets the resolution status.
        /// </summary>
        public AiRuntimePoolRouteResolutionStatus Status { get; init; }

        /// <summary>
        /// Gets the resolved route only when <see cref="Status"/> is
        /// <see cref="AiRuntimePoolRouteResolutionStatus.Resolved"/>.
        /// </summary>
        public AiRuntimePoolRouteDescriptor? Route { get; init; }
    }
}
