using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness
{
    /// <summary>
    /// Defines a provider-agnostic readiness waiter for runtime instances created through scale-out.
    /// </summary>
    /// <remarks>
    /// The readiness waiter validates that a runtime instance is visible and usable before a scale-out request
    /// is marked as fulfilled. It must not dispatch runs or mutate execution state.
    /// </remarks>
    public interface IAiRuntimeInstanceReadinessWaiter
    {
        /// <summary>
        /// Waits until the requested runtime instance is visible and able to accept work.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken = default);
    }
}