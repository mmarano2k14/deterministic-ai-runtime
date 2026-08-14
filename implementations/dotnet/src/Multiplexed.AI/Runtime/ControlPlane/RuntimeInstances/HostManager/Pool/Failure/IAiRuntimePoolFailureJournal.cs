using System;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Combines the write and read contracts for one authoritative runtime-pool failure journal.
    /// </summary>
    /// <remarks>
    /// The journal stores immutable failure facts. Capacity suppression and recovery claims remain
    /// separate concerns so a durable journal can be shared by parent hosts and the control plane
    /// without turning persistence into a second scheduler.
    /// </remarks>
    public interface IAiRuntimePoolFailureJournal :
        IAiRuntimePoolFailureObserver,
        IAiRuntimePoolFailureReader
    {
    }
}
