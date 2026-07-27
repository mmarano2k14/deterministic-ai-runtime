namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents a protocol-neutral forwarding result.
    /// </summary>
    /// <typeparam name="TResponse">The transport-adapter response type.</typeparam>
    public sealed record AiRuntimePoolRouteForwardingResult<TResponse>
    {
        /// <summary>
        /// Gets the exact route-resolution status.
        /// </summary>
        public AiRuntimePoolRouteResolutionStatus Status { get; init; }

        /// <summary>
        /// Gets the transport response when forwarding succeeded.
        /// </summary>
        public TResponse? Response { get; init; }

        /// <summary>
        /// Gets the exact route-incarnation identifier used for forwarding.
        /// </summary>
        public string? RouteId { get; init; }
    }
}
