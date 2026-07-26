using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Routing
{
    /// <summary>
    /// Represents one active forwarding operation against one exact route incarnation.
    /// </summary>
    public interface IAiRuntimePoolRouteLease :
        IAsyncDisposable
    {
        /// <summary>
        /// Gets the exact route acquired for the forwarding operation.
        /// </summary>
        AiRuntimePoolRouteDescriptor Route { get; }
    }
}
