using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Creates RuntimeInstanceOnly child-process launch and readiness plans.
    /// </summary>
    public interface IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory
    {
        /// <summary>
        /// Creates one launch and readiness plan for an independently identified child runtime.
        /// </summary>
        /// <param name="request">The authoritative child start request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The child runtime start plan.</returns>
        Task<AiRuntimeProcessPoolRuntimeInstanceStartPlan> CreateAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default);
    }
}
