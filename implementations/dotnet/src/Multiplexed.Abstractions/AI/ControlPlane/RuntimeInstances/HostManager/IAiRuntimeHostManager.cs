using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Defines the provider-agnostic runtime host manager boundary used by scale-out providers.
    /// </summary>
    /// <remarks>
    /// The runtime host manager owns runtime host lifecycle operations such as starting or attaching
    /// runtime instances. It does not dispatch runs, mutate DAG state, or bypass runtime queues.
    /// </remarks>
    public interface IAiRuntimeHostManager
    {
        /// <summary>
        /// Starts or attaches a runtime instance for a provider-specific scale-out request.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime host start result.</returns>
        Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default);
    }
}