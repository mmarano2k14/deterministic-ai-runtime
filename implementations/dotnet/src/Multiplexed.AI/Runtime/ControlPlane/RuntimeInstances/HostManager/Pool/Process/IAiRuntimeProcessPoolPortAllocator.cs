using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Reserves local TCP ports for independently addressed runtime pool child processes.
    /// </summary>
    public interface IAiRuntimeProcessPoolPortAllocator
    {
        /// <summary>
        /// Reserves one available local port from the inclusive configured range.
        /// </summary>
        /// <param name="basePort">The first candidate port.</param>
        /// <param name="maxPort">The final candidate port.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The reserved port lease.</returns>
        Task<IAiRuntimeProcessPoolPortLease> ReserveAsync(
            int basePort,
            int maxPort,
            CancellationToken cancellationToken = default);
    }
}
