using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Enumerates durable recoverable work assigned to one exact failed runtime instance.
    /// </summary>
    public interface IAiRuntimePoolAssignedWorkEnumerator
    {
        /// <summary>
        /// Enumerates work authorized by one exact failure observation.
        /// </summary>
        /// <param name="failureId">The immutable failure observation identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The read-only exact-runtime work inventory.</returns>
        Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
            string failureId,
            CancellationToken cancellationToken = default);
    }
}
