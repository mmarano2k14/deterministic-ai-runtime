using k8s;
using k8s.Models;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides a fake Kubernetes SDK client for runtime host lifecycle unit tests.
    /// </summary>
    public sealed class FakeAiKubernetesSdkClient : IAiKubernetesSdkClient
    {
        private readonly ConcurrentDictionary<string, IKubernetesObject> clusterCustomObjects =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, IKubernetesObject> namespacedCustomObjects =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the exception thrown when creating a pod.
        /// </summary>
        public Exception? CreatePodException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when listing Pods.
        /// </summary>
        public Exception? ListPodsException { get; set; }

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
        /// Gets or sets the exception thrown when listing services.
        /// </summary>
        public Exception? ListServicesException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when reading Endpoints.
        /// </summary>
        public Exception? ReadEndpointsException { get; set; }

        /// <summary>
        /// Gets or sets a callback invoked when Endpoints are read.
        /// </summary>
        public Action? ReadEndpointsCallback { get; set; }

        /// <summary>
        /// Gets or sets the Endpoints resource returned by Endpoints reads.
        /// </summary>
        public V1Endpoints? Endpoints { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when reading a cluster-scoped custom resource.
        /// </summary>
        public Exception? ReadClusterCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when creating a cluster-scoped custom resource.
        /// </summary>
        public Exception? CreateClusterCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when reading a namespaced custom resource.
        /// </summary>
        public Exception? ReadNamespacedCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when creating a namespaced custom resource.
        /// </summary>
        public Exception? CreateNamespacedCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when replacing a namespaced custom resource.
        /// </summary>
        public Exception? ReplaceNamespacedCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the exception thrown when deleting a namespaced custom resource.
        /// </summary>
        public Exception? DeleteNamespacedCustomObjectException { get; set; }

        /// <summary>
        /// Gets or sets the pod returned by pod status reads.
        /// </summary>
        public V1Pod? PodStatus { get; set; }

        /// <summary>
        /// Gets the Pods returned by Pod list operations.
        /// </summary>
        public IList<V1Pod> Pods { get; } = new List<V1Pod>();

        /// <summary>
        /// Gets the services returned by service list operations.
        /// </summary>
        public IList<V1Service> Services { get; } = new List<V1Service>();

        /// <summary>
        /// Gets the number of pod create calls.
        /// </summary>
        public int CreatePodCallCount { get; private set; }

        /// <summary>
        /// Gets the number of Pod list calls.
        /// </summary>
        public int ListPodsCallCount { get; private set; }

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
        /// Gets the number of service list calls.
        /// </summary>
        public int ListServicesCallCount { get; private set; }

        /// <summary>
        /// Gets the number of Endpoints read calls.
        /// </summary>
        public int ReadEndpointsCallCount { get; private set; }

        /// <summary>
        /// Gets the number of cluster custom-resource read calls.
        /// </summary>
        public int ReadClusterCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the number of cluster custom-resource create calls.
        /// </summary>
        public int CreateClusterCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the number of namespaced custom-resource read calls.
        /// </summary>
        public int ReadNamespacedCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the number of namespaced custom-resource create calls.
        /// </summary>
        public int CreateNamespacedCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the number of namespaced custom-resource replace calls.
        /// </summary>
        public int ReplaceNamespacedCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the number of namespaced custom-resource delete calls.
        /// </summary>
        public int DeleteNamespacedCustomObjectCallCount { get; private set; }

        /// <summary>
        /// Gets the last created pod.
        /// </summary>
        public V1Pod? LastCreatedPod { get; private set; }

        /// <summary>
        /// Gets the last created service.
        /// </summary>
        public V1Service? LastCreatedService { get; private set; }

        /// <summary>
        /// Gets the last created cluster-scoped custom resource.
        /// </summary>
        public IKubernetesObject? LastCreatedClusterCustomObject { get; private set; }

        /// <summary>
        /// Gets the last created namespaced custom resource.
        /// </summary>
        public IKubernetesObject? LastCreatedNamespacedCustomObject { get; private set; }

        /// <summary>
        /// Gets the last replaced namespaced custom resource.
        /// </summary>
        public IKubernetesObject? LastReplacedNamespacedCustomObject { get; private set; }

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

            pod.Metadata ??= new V1ObjectMeta();
            pod.Metadata.NamespaceProperty ??= namespaceName;

            this.Pods.Add(pod);

            return Task.FromResult(pod);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<V1Pod>> ListPodsAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            this.ListPodsCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.ListPodsException is not null)
            {
                throw this.ListPodsException;
            }

            var pods = this.Pods
                .Where(pod =>
                    string.Equals(
                        pod.Metadata?.NamespaceProperty,
                        namespaceName,
                        StringComparison.OrdinalIgnoreCase))
                .Where(pod =>
                    MatchesLabelSelector(
                        pod.Metadata,
                        labelSelector))
                .ToArray();

            return Task.FromResult<IReadOnlyList<V1Pod>>(pods);
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

            var matchingPods = this.Pods
                .Where(pod =>
                    string.Equals(
                        pod.Metadata?.Name,
                        podName,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        pod.Metadata?.NamespaceProperty,
                        namespaceName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var pod in matchingPods)
            {
                this.Pods.Remove(pod);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<V1Service> ReadServiceAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new V1Service
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = serviceName,
                        NamespaceProperty = namespaceName
                    },
                    Spec = new V1ServiceSpec
                    {
                        Type = "NodePort",
                        Ports =
                            new List<V1ServicePort>
                            {
                                new()
                                {
                                    Port = 8080,
                                    TargetPort = 8080,
                                    NodePort = 30080
                                }
                            }
                    }
                });
        }

        /// <inheritdoc />
        public Task<V1Endpoints> ReadEndpointsAsync(
            string serviceName,
            string namespaceName,
            CancellationToken cancellationToken = default)
        {
            this.ReadEndpointsCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            this.ReadEndpointsCallback?.Invoke();

            if (this.ReadEndpointsException is not null)
            {
                throw this.ReadEndpointsException;
            }

            var endpoints =
                this.Endpoints ??
                new V1Endpoints
                {
                    Metadata =
                        new V1ObjectMeta
                        {
                            Name = serviceName,
                            NamespaceProperty = namespaceName
                        }
                };

            return Task.FromResult(endpoints);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<V1Service>> ListServicesAsync(
            string namespaceName,
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            this.ListServicesCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.ListServicesException is not null)
            {
                throw this.ListServicesException;
            }

            var services = this.Services
                .Where(service =>
                    string.Equals(
                        service.Metadata?.NamespaceProperty,
                        namespaceName,
                        StringComparison.OrdinalIgnoreCase))
                .Where(service => MatchesLabelSelector(service.Metadata, labelSelector))
                .ToArray();

            return Task.FromResult<IReadOnlyList<V1Service>>(services);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<V1Service>> ListServicesForAllNamespacesAsync(
            string? labelSelector = null,
            CancellationToken cancellationToken = default)
        {
            this.ListServicesCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.ListServicesException is not null)
            {
                throw this.ListServicesException;
            }

            var services = this.Services
                .Where(service => MatchesLabelSelector(service.Metadata, labelSelector))
                .ToArray();

            return Task.FromResult<IReadOnlyList<V1Service>>(services);
        }

        /// <inheritdoc />
        public Task<T> ReadClusterCustomObjectAsync<T>(
            string group,
            string version,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.ReadClusterCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.ReadClusterCustomObjectException is not null)
            {
                throw this.ReadClusterCustomObjectException;
            }

            var key =
                CreateClusterCustomObjectKey(
                    group,
                    version,
                    plural,
                    name);

            return Task.FromResult(
                ReadStoredCustomObject<T>(
                    this.clusterCustomObjects,
                    key));
        }

        /// <inheritdoc />
        public Task<T> CreateClusterCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.CreateClusterCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(body);

            if (this.CreateClusterCustomObjectException is not null)
            {
                throw this.CreateClusterCustomObjectException;
            }

            var name = ResolveCustomObjectName(body);
            var key =
                CreateClusterCustomObjectKey(
                    group,
                    version,
                    plural,
                    name);

            if (!this.clusterCustomObjects.TryAdd(key, body))
            {
                throw new InvalidOperationException(
                    $"A fake Kubernetes cluster custom resource already exists for key '{key}'.");
            }

            this.LastCreatedClusterCustomObject = body;

            return Task.FromResult(body);
        }

        /// <inheritdoc />
        public Task<T> ReadNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.ReadNamespacedCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.ReadNamespacedCustomObjectException is not null)
            {
                throw this.ReadNamespacedCustomObjectException;
            }

            var key =
                CreateNamespacedCustomObjectKey(
                    group,
                    version,
                    namespaceName,
                    plural,
                    name);

            return Task.FromResult(
                ReadStoredCustomObject<T>(
                    this.namespacedCustomObjects,
                    key));
        }

        /// <inheritdoc />
        public Task<T> CreateNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.CreateNamespacedCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(body);

            if (this.CreateNamespacedCustomObjectException is not null)
            {
                throw this.CreateNamespacedCustomObjectException;
            }

            var name = ResolveCustomObjectName(body);
            var key =
                CreateNamespacedCustomObjectKey(
                    group,
                    version,
                    namespaceName,
                    plural,
                    name);

            if (!this.namespacedCustomObjects.TryAdd(key, body))
            {
                throw new InvalidOperationException(
                    $"A fake Kubernetes custom resource already exists for key '{key}'.");
            }

            this.LastCreatedNamespacedCustomObject = body;

            return Task.FromResult(body);
        }

        /// <inheritdoc />
        public Task<T> ReplaceNamespacedCustomObjectAsync<T>(
            T body,
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.ReplaceNamespacedCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(body);

            if (this.ReplaceNamespacedCustomObjectException is not null)
            {
                throw this.ReplaceNamespacedCustomObjectException;
            }

            var key =
                CreateNamespacedCustomObjectKey(
                    group,
                    version,
                    namespaceName,
                    plural,
                    name);

            this.namespacedCustomObjects[key] = body;
            this.LastReplacedNamespacedCustomObject = body;

            return Task.FromResult(body);
        }

        /// <inheritdoc />
        public Task DeleteNamespacedCustomObjectAsync<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            CancellationToken cancellationToken = default)
            where T : IKubernetesObject
        {
            this.DeleteNamespacedCustomObjectCallCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (this.DeleteNamespacedCustomObjectException is not null)
            {
                throw this.DeleteNamespacedCustomObjectException;
            }

            var key =
                CreateNamespacedCustomObjectKey(
                    group,
                    version,
                    namespaceName,
                    plural,
                    name);

            this.namespacedCustomObjects.TryRemove(key, out _);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Seeds a cluster-scoped custom resource for a unit test.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="value">The custom resource.</param>
        public void SetClusterCustomObject<T>(
            string group,
            string version,
            string plural,
            string name,
            T value)
            where T : IKubernetesObject
        {
            ArgumentNullException.ThrowIfNull(value);

            var key =
                CreateClusterCustomObjectKey(
                    group,
                    version,
                    plural,
                    name);

            this.clusterCustomObjects[key] = value;
        }

        /// <summary>
        /// Seeds a namespaced custom resource for a unit test.
        /// </summary>
        /// <typeparam name="T">The custom resource type.</typeparam>
        /// <param name="group">The Kubernetes API group.</param>
        /// <param name="version">The Kubernetes API version.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="plural">The custom resource plural name.</param>
        /// <param name="name">The resource name.</param>
        /// <param name="value">The custom resource.</param>
        public void SetNamespacedCustomObject<T>(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name,
            T value)
            where T : IKubernetesObject
        {
            ArgumentNullException.ThrowIfNull(value);

            var key =
                CreateNamespacedCustomObjectKey(
                    group,
                    version,
                    namespaceName,
                    plural,
                    name);

            this.namespacedCustomObjects[key] = value;
        }

        /// <summary>
        /// Reads a typed custom resource from the supplied fake store.
        /// </summary>
        private static T ReadStoredCustomObject<T>(
            ConcurrentDictionary<string, IKubernetesObject> store,
            string key)
            where T : IKubernetesObject
        {
            if (!store.TryGetValue(key, out var value))
            {
                throw new KeyNotFoundException(
                    $"No fake Kubernetes custom resource exists for key '{key}'.");
            }

            if (value is not T typedValue)
            {
                throw new InvalidOperationException(
                    $"The fake Kubernetes custom resource '{key}' has type '{value.GetType().FullName}', not '{typeof(T).FullName}'.");
            }

            return typedValue;
        }

        /// <summary>
        /// Resolves a Kubernetes custom-resource name from its metadata property.
        /// </summary>
        private static string ResolveCustomObjectName<T>(
            T body)
            where T : IKubernetesObject
        {
            var metadataProperty =
                body.GetType().GetProperty("Metadata");

            if (metadataProperty?.GetValue(body) is V1ObjectMeta metadata &&
                !string.IsNullOrWhiteSpace(metadata.Name))
            {
                return metadata.Name;
            }

            throw new InvalidOperationException(
                $"The fake Kubernetes custom resource type '{body.GetType().FullName}' must expose Metadata.Name.");
        }

        /// <summary>
        /// Determines whether Kubernetes metadata matches an exact-match label selector.
        /// </summary>
        private static bool MatchesLabelSelector(
            V1ObjectMeta? metadata,
            string? labelSelector)
        {
            if (string.IsNullOrWhiteSpace(labelSelector))
            {
                return true;
            }

            var labels = metadata?.Labels;

            if (labels is null)
            {
                return false;
            }

            foreach (var clause in labelSelector.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = clause.Split('=', 2, StringSplitOptions.TrimEntries);

                if (parts.Length != 2 ||
                    !labels.TryGetValue(parts[0], out var value) ||
                    !string.Equals(value, parts[1], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Creates a deterministic cluster-scoped custom-resource key.
        /// </summary>
        private static string CreateClusterCustomObjectKey(
            string group,
            string version,
            string plural,
            string name)
        {
            return $"{group}|{version}|{plural}|{name}";
        }

        /// <summary>
        /// Creates a deterministic namespaced custom-resource key.
        /// </summary>
        private static string CreateNamespacedCustomObjectKey(
            string group,
            string version,
            string namespaceName,
            string plural,
            string name)
        {
            return $"{group}|{version}|{namespaceName}|{plural}|{name}";
        }
    }
}
