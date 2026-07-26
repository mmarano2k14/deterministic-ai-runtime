using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents one independently managed runtime child process.
    /// </summary>
    public interface IAiRuntimeProcessPoolChild
    {
        /// <summary>
        /// Gets the logical runtime pool identifier.
        /// </summary>
        string PoolId { get; }

        /// <summary>
        /// Gets the immutable identifier of the exact pool host incarnation.
        /// </summary>
        string HostId { get; }

        /// <summary>
        /// Gets the independent runtime instance identifier.
        /// </summary>
        string RuntimeInstanceId { get; }

        /// <summary>
        /// Gets the monotonically increasing child ordinal within this manager incarnation.
        /// </summary>
        int Ordinal { get; }

        /// <summary>
        /// Gets the current child lifecycle status.
        /// </summary>
        AiRuntimeProcessPoolChildStatus Status { get; }

        /// <summary>
        /// Gets the task that completes exactly once when the child process exits or its lifecycle
        /// adapter fails.
        /// </summary>
        Task<AiRuntimeProcessPoolChildExit> Completion { get; }

        /// <summary>
        /// Stops the child process.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        Task StopAsync(
            CancellationToken cancellationToken = default);
    }
}
