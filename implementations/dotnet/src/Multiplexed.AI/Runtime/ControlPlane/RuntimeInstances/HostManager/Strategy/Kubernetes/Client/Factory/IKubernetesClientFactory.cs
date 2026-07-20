namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory
{
    /// <summary>
    /// Creates Kubernetes SDK operation clients used by the Kubernetes runtime host lifecycle adapter.
    /// </summary>
    /// <remarks>
    /// This factory keeps Kubernetes configuration loading outside of the runtime host strategy.
    /// Kubernetes remains a host lifecycle provider and does not become a runtime transport provider.
    /// </remarks>
    public interface IKubernetesClientFactory
    {
        /// <summary>
        /// Creates a Kubernetes SDK operation client.
        /// </summary>
        /// <returns>The Kubernetes SDK operation client.</returns>
        IAiKubernetesSdkClient CreateClient();
    }
}