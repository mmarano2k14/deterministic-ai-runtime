using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Enumerates durable recoverable work for one exact already-suppressed runtime identity.
    /// </summary>
    public interface IAiRuntimePoolSuppressedAssignedWorkEnumerator
    {
        /// <summary>
        /// Enumerates work only after confirming that the supplied suppression is authoritative.
        /// </summary>
        /// <param name="suppression">The exact immutable capacity suppression.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The read-only exact-runtime assigned-work inventory.</returns>
        Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
            AiRuntimePoolCapacitySuppression suppression,
            CancellationToken cancellationToken = default);
    }
}
