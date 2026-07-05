using k8s.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Provides a narrow Kubernetes SDK operation boundary for runtime host lifecycle operations.
    /// </summary>
    /// <remarks>
    /// This abstraction exists to keep the runtime host lifecycle client testable without requiring a real Kubernetes cluster.
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
    }
}