using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client
{
    /// <summary>
    /// Provides an in-memory Kubernetes Runtime Pool lifecycle client for focused tests.
    /// </summary>
    public sealed class FakeAiKubernetesRuntimePoolHostClient :
        IAiKubernetesRuntimePoolHostClient,
        IAiKubernetesRuntimePoolPodInventory
    {
        private readonly ConcurrentDictionary<
            string,
            AiKubernetesRuntimePoolPodSpec> createdPods = new();
        private readonly object creationSync = new();

        /// <summary>
        /// Gets or sets a value indicating whether creation should fail.
        /// </summary>
        public bool FailCreate { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether readiness should fail.
        /// </summary>
        public bool FailReadiness { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether deletion should fail.
        /// </summary>
        public bool FailDelete { get; set; }

        /// <summary>
        /// Gets the create call count.
        /// </summary>
        public int CreateCallCount { get; private set; }

        /// <summary>
        /// Gets the readiness call count.
        /// </summary>
        public int ReadinessCallCount { get; private set; }

        /// <summary>
        /// Gets the delete call count.
        /// </summary>
        public int DeleteCallCount { get; private set; }

        /// <summary>
        /// Gets the last created specification.
        /// </summary>
        public AiKubernetesRuntimePoolPodSpec? LastCreatedPodSpec
        {
            get;
            private set;
        }

        /// <inheritdoc />
        public Task<int> CountRuntimePoolPodsAsync(
            string namespaceName,
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            cancellationToken.ThrowIfCancellationRequested();

            var count =
                this.createdPods.Values.Count(
                    pod =>
                        StringComparer.OrdinalIgnoreCase.Equals(
                            pod.Namespace,
                            namespaceName.Trim()) &&
                        StringComparer.Ordinal.Equals(
                            pod.PoolId,
                            poolId.Trim()));

            return Task.FromResult(count);
        }

        /// <summary>
        /// Seeds one pre-existing physical Pod for a focused capacity test.
        /// </summary>
        public void SeedRuntimePoolPod(
            AiKubernetesRuntimePoolPodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            this.createdPods[podSpec.PodName] = podSpec;
        }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostCreateResult>
            CreateRuntimePoolHostAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            cancellationToken.ThrowIfCancellationRequested();

            this.CreateCallCount++;
            this.LastCreatedPodSpec = podSpec;

            if (this.FailCreate)
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostCreateResult.Rejected(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-runtime-pool-create-failed",
                        retryable: true));
            }

            lock (this.creationSync)
            {
                var physicalPodCount =
                    this.createdPods.Values.Count(
                        existing =>
                            StringComparer.OrdinalIgnoreCase.Equals(
                                existing.Namespace,
                                podSpec.Namespace) &&
                            StringComparer.Ordinal.Equals(
                                existing.PoolId,
                                podSpec.PoolId));

                if (podSpec.MaximumPodCount != int.MaxValue &&
                    physicalPodCount >= podSpec.MaximumPodCount)
                {
                    return Task.FromResult(
                        AiKubernetesRuntimeHostCreateResult.Rejected(
                            podSpec.Namespace,
                            podSpec.PodName,
                            "kubernetes-runtime-pool-physical-pod-capacity-already-satisfied",
                            retryable: true));
                }

                this.createdPods[podSpec.PodName] = podSpec;
            }

            return Task.FromResult(
                AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    string.Concat(
                        podSpec.PodName,
                        "-svc"),
                    CreateMetadata(podSpec)));
        }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostReadinessResult>
            WaitUntilHostReadyAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            cancellationToken.ThrowIfCancellationRequested();

            this.ReadinessCallCount++;

            if (this.FailReadiness)
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostReadinessResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-runtime-pool-readiness-failed",
                        timedOut: false,
                        retryable: true,
                        serviceName:
                            string.Concat(
                                podSpec.PodName,
                                "-svc"),
                        metadata: CreateMetadata(podSpec)));
            }

            if (!this.createdPods.ContainsKey(podSpec.PodName))
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostReadinessResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-runtime-pool-pod-not-created",
                        timedOut: false,
                        retryable: false));
            }

            return Task.FromResult(
                AiKubernetesRuntimeHostReadinessResult.Ready(
                    podSpec.Namespace,
                    podSpec.PodName,
                    string.Concat(
                        podSpec.PodName,
                        "-svc"),
                    CreateMetadata(podSpec)));
        }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostDeleteResult>
            DeleteRuntimePoolHostAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            cancellationToken.ThrowIfCancellationRequested();

            this.DeleteCallCount++;

            if (this.FailDelete)
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostDeleteResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-runtime-pool-delete-failed",
                        retryable: true));
            }

            this.createdPods.TryRemove(
                podSpec.PodName,
                out _);

            return Task.FromResult(
                AiKubernetesRuntimeHostDeleteResult.Deleted(
                    podSpec.Namespace,
                    podSpec.PodName,
                    string.Concat(
                        podSpec.PodName,
                        "-svc"),
                    CreateMetadata(podSpec)));
        }

        /// <summary>
        /// Creates deterministic fake lifecycle metadata.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiKubernetesRuntimePoolPodSpec podSpec)
        {
            var hostId =
                string.Concat(
                    "fake-pod-uid-",
                    podSpec.PodRequestId);

            var endpoint =
                string.Concat(
                    "http://",
                    podSpec.PodName,
                    "-svc.",
                    podSpec.Namespace,
                    ".svc.cluster.local:",
                    podSpec.Bootstrap.StableTransportPort);

            return new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeHostMetadataKeys.HostId] = hostId,
                [AiKubernetesRuntimeHostMetadataKeys.PodUid] = hostId,
                [AiKubernetesRuntimeHostMetadataKeys.PodName] = podSpec.PodName,
                [AiKubernetesRuntimeHostMetadataKeys.Namespace] = podSpec.Namespace,
                [AiRuntimePoolMetadataKeys.PoolId] = podSpec.PoolId,
                [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] = endpoint
            };
        }
    }
}
