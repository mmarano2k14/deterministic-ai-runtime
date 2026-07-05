using k8s;
using k8s.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Provides Kubernetes SDK operations for runtime host lifecycle operations.
    /// </summary>
    public sealed class AiKubernetesSdkClient : IAiKubernetesSdkClient
    {
        private readonly IKubernetes client;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiKubernetesSdkClient"/> class.
        /// </summary>
        /// <param name="client">The Kubernetes SDK client.</param>
        public AiKubernetesSdkClient(IKubernetes client)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <inheritdoc />
        public Task<V1Pod> CreatePodAsync(
            V1Pod pod,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.CreateNamespacedPodAsync(
                pod,
                namespaceName,
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task<V1Service> CreateServiceAsync(
            V1Service service,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.CreateNamespacedServiceAsync(
                service,
                namespaceName,
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task<V1Pod> ReadPodStatusAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.ReadNamespacedPodStatusAsync(
                podName,
                namespaceName,
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task DeleteServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.DeleteNamespacedServiceAsync(
                serviceName,
                namespaceName,
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task DeletePodAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.DeleteNamespacedPodAsync(
                podName,
                namespaceName,
                cancellationToken: cancellationToken);
        }
    }
}