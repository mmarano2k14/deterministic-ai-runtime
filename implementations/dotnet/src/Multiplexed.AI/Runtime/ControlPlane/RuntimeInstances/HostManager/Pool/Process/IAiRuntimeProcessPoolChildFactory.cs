using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Creates independently managed runtime child processes for a process-host runtime pool.
    /// </summary>
    public interface IAiRuntimeProcessPoolChildFactory
    {
        /// <summary>
        /// Starts one runtime child process.
        /// </summary>
        /// <param name="request">The typed child-process start request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The started child-process handle.</returns>
        Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default);
    }
}
