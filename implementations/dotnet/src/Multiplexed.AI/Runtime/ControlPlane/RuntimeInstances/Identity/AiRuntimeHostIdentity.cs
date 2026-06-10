using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Identity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Identity
{
    /// <summary>
    /// Provides a unique runtime host identity for the current process.
    /// </summary>
    public sealed class AiRuntimeHostIdentity : IAiRuntimeHostIdentity
    {
        public AiRuntimeHostIdentity()
        {
            HostId =
                $"host-{Guid.NewGuid():N}";
        }

        public string HostId { get; }
    }
}