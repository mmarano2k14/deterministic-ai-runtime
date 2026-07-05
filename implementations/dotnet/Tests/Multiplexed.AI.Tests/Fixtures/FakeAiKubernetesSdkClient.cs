using k8s.Models;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides a fake Kubernetes SDK client for runtime host lifecycle unit tests.
    /// </summary>
    public sealed class FakeAiKubernetesSdkClient : IAiKubernetesSdkClient
    {
        /// <summary>
        /// Gets or sets the exception thrown when creating a pod.
        /// </summary>
        public Exception? CreatePodException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when creating a service.
        /// </summary>
        public Exception? CreateServiceException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when reading pod status.
        /// </summary>
        public Exception? ReadPodStatusException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when deleting a service.
        /// </summary>
        public Exception? DeleteServiceException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when deleting a pod.
        /// </summary>
        public Exception? DeletePodException { get; set; }

        /// <summary>
        /// Gets or sets the pod returned by pod status reads.
        /// </summary>
        public V1Pod? PodStatus { get; set; }

        /// <summary>
        /// Gets the number of pod create calls.
        /// </summary>
        public int CreatePodCallCount { get; private set; }

        /// <summary>
        /// Gets the number of service create calls.
        /// </summary>
        public int CreateServiceCallCount { get; private set; }

        /// <summary>
        /// Gets the number of pod status read calls.
        /// </summary>
        public int ReadPodStatusCallCount { get; private set; }

        /// <summary>
        /// Gets the number of service delete calls.
        /// </summary>
        public int DeleteServiceCallCount { get; private set; }

        /// <summary>
        /// Gets the number of pod delete calls.
        /// </summary>
        public int DeletePodCallCount { get; private set; }

        /// <summary>
        /// Gets the last created pod.
        /// </summary>
        public V1Pod? LastCreatedPod { get; private set; }

        /// <summary>
        /// Gets the last created service.
        /// </summary>
        public V1Service? LastCreatedService { get; private set; }

        /// <inheritdoc />
        public Task<V1Pod> CreatePodAsync(
            V1Pod pod,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.CreatePodCallCount++;
            this.LastCreatedPod = pod;

            if (this.CreatePodException is not null)
            {
                throw this.CreatePodException;
            }

            return Task.FromResult(pod);
        }

        /// <inheritdoc />
        public Task<V1Service> CreateServiceAsync(
            V1Service service,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.CreateServiceCallCount++;
            this.LastCreatedService = service;

            if (this.CreateServiceException is not null)
            {
                throw this.CreateServiceException;
            }

            return Task.FromResult(service);
        }

        /// <inheritdoc />
        public Task<V1Pod> ReadPodStatusAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.ReadPodStatusCallCount++;

            if (this.ReadPodStatusException is not null)
            {
                throw this.ReadPodStatusException;
            }

            return Task.FromResult(this.PodStatus ?? new V1Pod());
        }

        /// <inheritdoc />
        public Task DeleteServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.DeleteServiceCallCount++;

            if (this.DeleteServiceException is not null)
            {
                throw this.DeleteServiceException;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeletePodAsync(
            string podName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.DeletePodCallCount++;

            if (this.DeletePodException is not null)
            {
                throw this.DeletePodException;
            }

            return Task.CompletedTask;
        }
    }
}