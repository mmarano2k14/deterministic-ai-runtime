using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure
{
    /// <summary>
    /// Records authoritative runtime-pool failure observations.
    /// </summary>
    public interface IAiRuntimePoolFailureObserver
    {
        /// <summary>
        /// Records one exact immutable failure observation.
        /// </summary>
        /// <param name="observation">The failure observation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative stored observation.</returns>
        Task<AiRuntimePoolFailureObservation> RecordAsync(
            AiRuntimePoolFailureObservation observation,
            CancellationToken cancellationToken = default);
    }
}
