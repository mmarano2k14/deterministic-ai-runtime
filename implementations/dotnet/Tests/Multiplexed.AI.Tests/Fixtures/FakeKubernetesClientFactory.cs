using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides a fake Kubernetes client factory for runtime host lifecycle unit tests.
    /// </summary>
    public sealed class FakeKubernetesClientFactory : IKubernetesClientFactory
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FakeKubernetesClientFactory"/> class.
        /// </summary>
        /// <param name="client">The fake Kubernetes SDK client.</param>
        public FakeKubernetesClientFactory(IAiKubernetesSdkClient client)
        {
            this.Client = client;
        }

        /// <summary>
        /// Gets the fake Kubernetes SDK client.
        /// </summary>
        public IAiKubernetesSdkClient Client { get; }

        /// <inheritdoc />
        public IAiKubernetesSdkClient CreateClient()
        {
            return this.Client;
        }
    }
}