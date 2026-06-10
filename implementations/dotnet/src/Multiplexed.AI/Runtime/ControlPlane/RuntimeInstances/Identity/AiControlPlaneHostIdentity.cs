using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Identity
{
    /// <summary>
    /// Provides a stable unique control-plane host identity for the lifetime of the current process.
    /// </summary>
    public sealed class AiControlPlaneHostIdentity : IAiControlPlaneHostIdentity
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiControlPlaneHostIdentity"/> class.
        /// </summary>
        public AiControlPlaneHostIdentity()
        {
            ControlPlaneHostId =
                $"control-plane-{Guid.NewGuid():N}";
        }

        /// <inheritdoc />
        public string ControlPlaneHostId { get; }
    }
}