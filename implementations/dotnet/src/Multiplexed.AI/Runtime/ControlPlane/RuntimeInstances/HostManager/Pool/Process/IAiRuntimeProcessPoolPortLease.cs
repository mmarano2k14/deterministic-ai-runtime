using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents one reserved local TCP port owned by a runtime pool child lifecycle.
    /// </summary>
    public interface IAiRuntimeProcessPoolPortLease : IAsyncDisposable
    {
        /// <summary>
        /// Gets the reserved local TCP port.
        /// </summary>
        int Port { get; }
    }
}
