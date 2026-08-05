namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client
{
    /// <summary>
    /// Reads the physical Kubernetes Pod inventory for one logical Runtime Pool.
    /// </summary>
    /// <remarks>
    /// This is the final physical capacity authority. Runtime registry membership is
    /// intentionally not used because a live Pod can be temporarily absent from the
    /// registry while its child runtimes register, heartbeat, or transition state.
    /// </remarks>
    public interface IAiKubernetesRuntimePoolPodInventory
    {
        /// <summary>
        /// Counts every Kubernetes Pod that still physically exists for the exact PoolId.
        /// </summary>
        Task<int> CountRuntimePoolPodsAsync(
            string namespaceName,
            string poolId,
            CancellationToken cancellationToken = default);
    }
}
