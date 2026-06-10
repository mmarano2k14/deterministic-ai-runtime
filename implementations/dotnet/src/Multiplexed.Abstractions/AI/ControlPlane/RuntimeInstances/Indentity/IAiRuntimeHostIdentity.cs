namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity
{
    /// <summary>
    /// Provides the unique identity of the current runtime host process.
    /// </summary>
    public interface IAiRuntimeHostIdentity
    {
        /// <summary>
        /// Gets the unique host id generated for the current host process.
        /// </summary>
        string HostId { get; }
    }
}