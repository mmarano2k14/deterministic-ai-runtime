using k8s;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory
{
    /// <summary>
    /// Creates Kubernetes SDK clients used by the Kubernetes runtime host lifecycle adapter.
    /// </summary>
    /// <remarks>
    /// This factory keeps Kubernetes configuration loading outside of the runtime host strategy.
    /// Kubernetes remains a host lifecycle provider and does not become a runtime transport provider.
    /// </remarks>
    public interface IKubernetesClientFactory
    {
        /// <summary>
        /// Creates a Kubernetes SDK client.
        /// </summary>
        /// <returns>The Kubernetes SDK client.</returns>
        IKubernetes CreateClient();
    }
}