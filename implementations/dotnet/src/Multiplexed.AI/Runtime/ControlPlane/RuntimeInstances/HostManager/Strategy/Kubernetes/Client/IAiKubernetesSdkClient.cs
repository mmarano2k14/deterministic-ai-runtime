using k8s;
using k8s.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Provides a narrow Kubernetes SDK operation boundary for runtime host lifecycle operations.
    /// </summary>
    /// <remarks>
    /// This abstraction exists to keep the runtime host lifecycle client testable without requiring a real Kubernetes cluster.
    /// It supports both core Kubernetes resources and typed custom resources used by the Kubernetes Gateway API.
    /// It does not represent a runtime command transport.
    /// </remarks>
    public interface IAiKubernetesSdkClient
    {
        /// <summary>
        /// Creates a Kubernetes pod.
        /// </summary>
        /// <param name="pod">The pod to create.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created pod.</returns>
        Task<V1Pod> CreatePodAsync(
            V1Pod pod,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a Kubernetes service.
        /// </summary>
        /// <param name="service">The service to create.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created service.</returns>
        Task<V1Service> CreateServiceAsync(
            V1Service service,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a Kubernetes service.
        /// </summary>
        /// <param name="serviceName">The service name.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The Kubernetes service.</returns>
        Task<V1Service> ReadServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists Kubernetes services in a namespace.
        /// </summary>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="labelSelector">The optional Kubernetes label selector.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The matching Kubernetes services.</returns>
        Task<IReadOnlyList<V1Service>> ListServicesAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists Kubernetes Services across all namespaces.
        /// </summary>
        /// <param name="labelSelector">The optional Kubernetes label selector.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The matching Kubernetes Services.</returns>
        Task<IReadOnlyList<V1Service>> ListServicesForAllNamespacesAsync(
            string? labelSelector = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads the status of a Kubernetes pod.
        /// </summary>
        /// <param name="podName">The pod name.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pod status.</returns>
        Task<V1Pod> ReadPodStatusAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a Kubernetes service.
        /// </summary>
        /// <param name="serviceName">The service name.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the delete operation.</returns>
        Task DeleteServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a Kubernetes pod.
        /// </summary>
        /// <param name="podName">The pod name.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the delete operation.</returns>
        Task DeletePodAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a typed cluster-scoped Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The custom resource.</returns>
        Task<T> ReadClusterCustomObjectAsync<T>(
            string group,
            string version,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;

        /// <summary>
        /// Creates a typed cluster-scoped Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="body">The custom resource body.</param>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created custom resource.</returns>
        Task<T> CreateClusterCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;

        /// <summary>
        /// Reads a typed namespaced Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The custom resource.</returns>
        Task<T> ReadNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;

        /// <summary>
        /// Creates a typed namespaced Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="body">The custom resource body.</param>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created custom resource.</returns>
        Task<T> CreateNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;

        /// <summary>
        /// Replaces a typed namespaced Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="body">The replacement custom resource body.</param>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The replaced custom resource.</returns>
        Task<T> ReplaceNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;

        /// <summary>
        /// Deletes a typed namespaced Kubernetes custom resource.
        /// </summary>
        /// <typeparam name="T">The custom resource type returned by Kubernetes.</typeparam>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the delete operation.</returns>
        Task DeleteNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject;
    }
}
