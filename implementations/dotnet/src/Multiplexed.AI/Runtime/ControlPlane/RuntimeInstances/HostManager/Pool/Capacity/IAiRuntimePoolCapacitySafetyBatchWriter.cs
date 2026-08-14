using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Atomically suppresses a complete set of exact Runtime Pool capacity identities.
    /// </summary>
    public interface IAiRuntimePoolCapacitySafetyBatchWriter
    {
        /// <summary>
        /// Stores every suppression as one atomic visibility boundary.
        /// </summary>
        /// <param name="suppressions">The complete exact suppression set.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative stored suppressions.</returns>
        Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
            SuppressBatchAsync(
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions,
                CancellationToken cancellationToken = default);
    }
}
