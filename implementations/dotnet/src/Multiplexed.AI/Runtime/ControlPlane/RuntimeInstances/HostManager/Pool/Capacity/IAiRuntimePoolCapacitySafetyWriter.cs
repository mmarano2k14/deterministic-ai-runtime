using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Suppresses exact unsafe runtime-pool capacity.
    /// </summary>
    public interface IAiRuntimePoolCapacitySafetyWriter
    {
        /// <summary>
        /// Permanently suppresses one immutable runtime instance.
        /// </summary>
        /// <param name="suppression">The exact suppression authority.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative stored suppression.</returns>
        Task<AiRuntimePoolCapacitySuppression> SuppressAsync(
            AiRuntimePoolCapacitySuppression suppression,
            CancellationToken cancellationToken = default);
    }
}
