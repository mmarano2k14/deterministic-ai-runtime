using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents the complete process, endpoint, port, and readiness plan for one child runtime.
    /// </summary>
    public sealed record AiRuntimeProcessPoolRuntimeInstanceStartPlan
    {
        /// <summary>
        /// Gets the reserved child transport port lease.
        /// </summary>
        public required IAiRuntimeProcessPoolPortLease PortLease { get; init; }

        /// <summary>
        /// Gets the child transport endpoint.
        /// </summary>
        public required string TransportEndpoint { get; init; }

        /// <summary>
        /// Gets the operating-system child-process options.
        /// </summary>
        public required AiRuntimeProcessPoolChildProcessOptions ProcessOptions { get; init; }

        /// <summary>
        /// Gets the provider-neutral registry, capacity, and transport readiness request.
        /// </summary>
        public required AiRuntimeInstanceReadinessRequest ReadinessRequest { get; init; }
    }
}
