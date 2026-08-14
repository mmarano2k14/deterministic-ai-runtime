using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    /// <summary>
    /// Executes a claimed failed-Pod inventory while its membership lease remains active.
    /// </summary>
    public interface IAiKubernetesRuntimePoolPodClaimedRecoveryExecutor
    {
        Task<AiKubernetesRuntimePoolPodClaimedRecoveryExecutionResult>
            ExecuteAsync(
                AiKubernetesRuntimePoolPodClaimedAssignedWork claimedWork,
                CancellationToken cancellationToken = default);
    }
}
