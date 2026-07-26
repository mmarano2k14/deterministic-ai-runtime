using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts one operating-system child from a dynamically created process launch plan.
    /// </summary>
    public interface IAiRuntimeProcessPoolChildProcessLauncher
    {
        /// <summary>
        /// Starts one child process using the supplied authoritative identity and process options.
        /// </summary>
        /// <param name="request">The authoritative child start request.</param>
        /// <param name="options">The operating-system process options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The started child handle.</returns>
        Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            AiRuntimeProcessPoolChildProcessOptions options,
            CancellationToken cancellationToken = default);
    }
}
