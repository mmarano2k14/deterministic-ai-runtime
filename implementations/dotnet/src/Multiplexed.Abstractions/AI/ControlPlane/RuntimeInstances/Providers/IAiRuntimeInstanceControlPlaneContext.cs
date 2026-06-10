using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Defines a shared control-plane context used before dispatching,
    /// controlling, or querying runtime instances through providers.
    /// </summary>
    public interface IAiRuntimeInstanceControlPlaneContext : IAiRuntimeInstanceProvider
    {
        /// <summary>
        /// Gets the current control-plane identity.
        /// </summary>
        IAiControlPlaneHostIdentity? Identity { get; }

        /// <summary>
        /// Sets the current control-plane identity.
        /// </summary>
        /// <param name="identity">The control-plane identity to apply.</param>
        void SetControlPlaneIdentity(
            IAiControlPlaneHostIdentity identity);
    }
}