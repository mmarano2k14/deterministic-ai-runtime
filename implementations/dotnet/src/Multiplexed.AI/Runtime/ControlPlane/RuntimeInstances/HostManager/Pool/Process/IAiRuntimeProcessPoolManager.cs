using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Manages the deterministic local lifecycle of runtime child processes in one pool host.
    /// </summary>
    public interface IAiRuntimeProcessPoolManager
    {
        /// <summary>
        /// Gets the immutable identity of this process pool manager incarnation.
        /// </summary>
        AiRuntimeProcessPoolIdentity Identity { get; }

        /// <summary>
        /// Ensures the configured initial child-process capacity.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pool snapshot after capacity reconciliation.</returns>
        Task<AiRuntimeProcessPoolSnapshot> EnsureInitialCapacityAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensures that at least the requested number of child processes is tracked.
        /// </summary>
        /// <param name="requiredProcessCount">The required child-process count.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pool snapshot after capacity reconciliation.</returns>
        Task<AiRuntimeProcessPoolSnapshot> EnsureCapacityAsync(
            int requiredProcessCount,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the current pool lifecycle snapshot.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The current pool lifecycle snapshot.</returns>
        Task<AiRuntimeProcessPoolSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Stops every tracked child process in deterministic reverse-start order.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous stop operation.</returns>
        Task StopAsync(
            CancellationToken cancellationToken = default);
    }
}
