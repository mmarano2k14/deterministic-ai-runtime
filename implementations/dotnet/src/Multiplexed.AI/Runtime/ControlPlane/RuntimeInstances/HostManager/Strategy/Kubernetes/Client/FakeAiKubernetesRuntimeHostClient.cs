using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Provides an in-memory Kubernetes runtime host client for tests and local strategy validation.
    /// </summary>
    /// <remarks>
    /// This client does not talk to a Kubernetes cluster.
    /// It records runtime host specifications in memory and returns deterministic lifecycle results.
    /// </remarks>
    public sealed class FakeAiKubernetesRuntimeHostClient : IAiKubernetesRuntimeHostClient
    {
        private readonly ConcurrentDictionary<string, AiKubernetesRuntimePodSpec> createdPods = new();

        /// <summary>
        /// Gets or sets a value indicating whether create operations should fail.
        /// </summary>
        public bool FailCreate { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether host readiness operations should fail.
        /// </summary>
        public bool FailReadiness { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether delete operations should fail.
        /// </summary>
        public bool FailDelete { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether readiness failures should be reported as timeouts.
        /// </summary>
        public bool ReadinessTimedOut { get; set; }

        /// <summary>
        /// Gets the number of create calls.
        /// </summary>
        public int CreateCallCount { get; private set; }

        /// <summary>
        /// Gets the number of readiness wait calls.
        /// </summary>
        public int ReadinessCallCount { get; private set; }

        /// <summary>
        /// Gets the number of delete calls.
        /// </summary>
        public int DeleteCallCount { get; private set; }

        /// <summary>
        /// Gets the last pod specification passed to create.
        /// </summary>
        public AiKubernetesRuntimePodSpec? LastCreatedPodSpec { get; private set; }

        /// <summary>
        /// Gets the last pod specification passed to readiness.
        /// </summary>
        public AiKubernetesRuntimePodSpec? LastReadinessPodSpec { get; private set; }

        /// <summary>
        /// Gets the last pod specification passed to delete.
        /// </summary>
        public AiKubernetesRuntimePodSpec? LastDeletedPodSpec { get; private set; }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostCreateResult> CreateRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
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
                        "fake-kubernetes-create-failed",
                        retryable: true));
            }

            this.createdPods[podSpec.PodName] = podSpec;

            return Task.FromResult(
                AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    ResolveServiceName(podSpec)));
        }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostReadinessResult> WaitUntilHostReadyAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            cancellationToken.ThrowIfCancellationRequested();

            this.ReadinessCallCount++;
            this.LastReadinessPodSpec = podSpec;

            if (this.FailReadiness)
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostReadinessResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-readiness-failed",
                        timedOut: this.ReadinessTimedOut,
                        retryable: true,
                        serviceName: ResolveServiceName(podSpec)));
            }

            if (!this.createdPods.ContainsKey(podSpec.PodName))
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostReadinessResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-pod-not-created",
                        timedOut: false,
                        retryable: false,
                        serviceName: ResolveServiceName(podSpec)));
            }

            return Task.FromResult(
                AiKubernetesRuntimeHostReadinessResult.Ready(
                    podSpec.Namespace,
                    podSpec.PodName,
                    ResolveServiceName(podSpec)));
        }

        /// <inheritdoc />
        public Task<AiKubernetesRuntimeHostDeleteResult> DeleteRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);
            cancellationToken.ThrowIfCancellationRequested();

            this.DeleteCallCount++;
            this.LastDeletedPodSpec = podSpec;

            if (this.FailDelete)
            {
                return Task.FromResult(
                    AiKubernetesRuntimeHostDeleteResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        "fake-kubernetes-delete-failed",
                        retryable: true,
                        serviceName: ResolveServiceName(podSpec)));
            }

            this.createdPods.TryRemove(podSpec.PodName, out _);

            return Task.FromResult(
                AiKubernetesRuntimeHostDeleteResult.Deleted(
                    podSpec.Namespace,
                    podSpec.PodName,
                    ResolveServiceName(podSpec)));
        }

        /// <summary>
        /// Resolves the deterministic fake service name for a pod specification.
        /// </summary>
        /// <param name="podSpec">The pod specification.</param>
        /// <returns>The fake service name.</returns>
        private static string ResolveServiceName(
            AiKubernetesRuntimePodSpec podSpec)
        {
            return $"{podSpec.PodName}-svc";
        }
    }
}