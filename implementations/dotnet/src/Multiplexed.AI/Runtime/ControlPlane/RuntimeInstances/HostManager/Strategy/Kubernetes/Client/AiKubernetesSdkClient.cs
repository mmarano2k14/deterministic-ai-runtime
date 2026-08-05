using k8s;
using k8s.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
        public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            var podList =
                await this.client.CoreV1
                    .ListNamespacedPodAsync(
                        namespaceParameter: namespaceName,
                        labelSelector: labelSelector,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return podList.Items?.ToArray() ?? Array.Empty<V1Pod>();
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

        /// <inheritdoc />
        public Task<V1Service> ReadServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            return this.client.CoreV1.ReadNamespacedServiceAsync(
                name: serviceName,
                namespaceParameter: namespaceName,
                cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<V1Service>> ListServicesAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            var serviceList =
                await this.client.CoreV1
                    .ListNamespacedServiceAsync(
                        namespaceParameter: namespaceName,
                        labelSelector: labelSelector,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return serviceList.Items?.ToArray() ?? Array.Empty<V1Service>();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<V1Service>> ListServicesForAllNamespacesAsync(
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            var serviceList =
                await this.client.CoreV1
                    .ListServiceForAllNamespacesAsync(
                        labelSelector: labelSelector,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return serviceList.Items?.ToArray() ?? Array.Empty<V1Service>();
        }

        /// <inheritdoc />
        public async Task<T> ReadClusterCustomObjectAsync<T>(
            string group,
            string version,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            return await genericClient
                .ReadAsync<T>(
                    name,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> CreateClusterCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            ArgumentNullException.ThrowIfNull(body);

            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            return await genericClient
                .CreateAsync(
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ReadNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            return await genericClient
                .ReadNamespacedAsync<T>(
                    namespaceName,
                    name,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> CreateNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            ArgumentNullException.ThrowIfNull(body);

            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            return await genericClient
                .CreateNamespacedAsync(
                    body,
                    namespaceName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<T> ReplaceNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            ArgumentNullException.ThrowIfNull(body);

            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            return await genericClient
                .ReplaceNamespacedAsync(
                    body,
                    namespaceName,
                    name,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            using var genericClient =
                CreateGenericClient(
                    group,
                    version,
                    plural);

            _ = await genericClient
                .DeleteNamespacedAsync<T>(
                    namespaceName,
                    name,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a non-owning typed Kubernetes custom-resource client.
        /// </summary>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <returns>The generic Kubernetes client.</returns>
        private GenericClient CreateGenericClient(
            string group,
            string version,
            string plural)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group);
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            ArgumentException.ThrowIfNullOrWhiteSpace(plural);

            return new GenericClient(
                this.client,
                group,
                version,
                plural,
                disposeClient: false);
        }
    }
}
