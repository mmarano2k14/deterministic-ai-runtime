using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution
{
    /// <summary>
    /// Executes existing recovery transitions while one exact recovery claim remains active.
    /// </summary>
    public interface IAiRuntimePoolClaimedRecoveryExecutor
    {
        /// <summary>
        /// Executes deterministic transitions for the exact claimed assigned-work inventory.
        /// </summary>
        /// <remarks>
        /// This method does not release the claim lease. The caller owns release after observing
        /// the complete deterministic result.
        /// </remarks>
        Task<AiRuntimePoolClaimedRecoveryExecutionResult> ExecuteAsync(
            AiRuntimePoolClaimedAssignedWork claimedWork,
            CancellationToken cancellationToken = default);
    }
}
