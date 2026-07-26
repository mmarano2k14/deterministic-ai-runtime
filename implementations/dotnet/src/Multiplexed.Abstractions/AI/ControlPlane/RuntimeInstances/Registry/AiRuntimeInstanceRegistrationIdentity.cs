namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Represents the authoritative pool and host identities resolved for one runtime registration.
    /// </summary>
    public sealed record AiRuntimeInstanceRegistrationIdentity
    {
        /// <summary>
        /// Gets the optional logical runtime pool identifier.
        /// </summary>
        public string? PoolId { get; init; }

        /// <summary>
        /// Gets the optional immutable identifier of the exact host incarnation.
        /// </summary>
        public string? HostId { get; init; }
    }
}
