using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Reads authoritative runtime-pool failure observations.
    /// </summary>
    public interface IAiRuntimePoolFailureReader
    {
        /// <summary>
        /// Gets one failure observation by immutable identifier.
        /// </summary>
        Task<AiRuntimePoolFailureObservation?> GetByFailureIdAsync(
            string failureId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists failure observations belonging to one exact host incarnation.
        /// </summary>
        Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
            ListByHostIdAsync(
                string hostId,
                CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists failure observations belonging to one exact runtime instance.
        /// </summary>
        Task<IReadOnlyList<AiRuntimePoolFailureObservation>>
            ListByRuntimeInstanceIdAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default);
    }
}
