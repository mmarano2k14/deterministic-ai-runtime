using k8s;
using KubernetesSdkClient = k8s.Kubernetes;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory
{
    /// <summary>
    /// Creates Kubernetes SDK clients from the current Kubernetes execution environment.
    /// </summary>
    public sealed class DefaultKubernetesClientFactory : IKubernetesClientFactory
    {
        /// <inheritdoc />
        public IKubernetes CreateClient()
        {
            KubernetesClientConfiguration configuration;

            if (KubernetesClientConfiguration.IsInCluster())
            {
                configuration = KubernetesClientConfiguration.InClusterConfig();
            }
            else
            {
                configuration = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            }

            return new KubernetesSdkClient(configuration);
        }
    }
}