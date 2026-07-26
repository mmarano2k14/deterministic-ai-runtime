namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents the result of atomically acquiring one exact forwarding route.
    /// </summary>
    public sealed record AiRuntimePoolRouteLeaseAcquisitionResult
    {
        /// <summary>
        /// Gets the route-resolution status.
        /// </summary>
        public AiRuntimePoolRouteResolutionStatus Status { get; init; }

        /// <summary>
        /// Gets the active route lease only when <see cref="Status"/> is
        /// <see cref="AiRuntimePoolRouteResolutionStatus.Resolved"/>.
        /// </summary>
        public IAiRuntimePoolRouteLease? Lease { get; init; }
    }
}
