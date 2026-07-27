using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Reads exact unsafe runtime-pool capacity.
    /// </summary>
    public interface IAiRuntimePoolCapacitySafetyReader
    {
        /// <summary>
        /// Gets the suppression for one exact runtime identity, when present.
        /// </summary>
        Task<AiRuntimePoolCapacitySuppression?> GetSuppressionAsync(
            string poolId,
            string hostId,
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists every suppressed runtime instance belonging to one exact host incarnation.
        /// </summary>
        Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
            ListByHostIdAsync(
                string hostId,
                CancellationToken cancellationToken = default);
    }
}
