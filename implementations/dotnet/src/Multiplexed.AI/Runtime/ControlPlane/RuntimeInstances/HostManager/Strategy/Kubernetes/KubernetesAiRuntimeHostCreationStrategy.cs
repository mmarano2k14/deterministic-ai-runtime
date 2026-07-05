using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes
{
    /// <summary>
    /// Provides a Kubernetes runtime host creation strategy.
    /// </summary>
    /// <remarks>
    /// This strategy represents Kubernetes as a runtime host lifecycle provider.
    /// It creates Kubernetes-level runtime host resources through <see cref="IAiKubernetesRuntimeHostClient" />,
    /// then optionally waits for runtime-level readiness through <see cref="IAiRuntimeInstanceReadinessWaiter" />.
    /// Runtime command dispatch remains owned by the configured transport provider, such as HTTP or gRPC.
    /// </remarks>
    public sealed class KubernetesAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy
    {
        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly AiKubernetesRuntimePodSpecBuilder podSpecBuilder;
        private readonly IAiKubernetesRuntimeHostClient client;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesAiRuntimeHostCreationStrategy"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="podSpecBuilder">The Kubernetes runtime pod specification builder.</param>
        /// <param name="client">The Kubernetes runtime host client.</param>
        /// <param name="readinessWaiter">The runtime instance readiness waiter.</param>
        public KubernetesAiRuntimeHostCreationStrategy(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            AiKubernetesRuntimePodSpecBuilder podSpecBuilder,
            IAiKubernetesRuntimeHostClient client,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter)
        {
            ArgumentNullException.ThrowIfNull(options);

            this.options = options.Value ?? throw new ArgumentException("Kubernetes runtime host options are required.", nameof(options));
            this.podSpecBuilder = podSpecBuilder ?? throw new ArgumentNullException(nameof(podSpecBuilder));
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
        }

        /// <inheritdoc />
        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Kubernetes;

        /// <inheritdoc />
        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!this.options.Enabled)
            {
                return CreateRejectedResult(
                    request,
                    "kubernetes-runtime-host-creation-disabled",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.Namespace))
            {
                return CreateRejectedResult(
                    request,
                    "kubernetes-runtime-namespace-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeImage))
            {
                return CreateRejectedResult(
                    request,
                    "kubernetes-runtime-image-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            if (string.IsNullOrWhiteSpace(this.options.ContainerName))
            {
                return CreateRejectedResult(
                    request,
                    "kubernetes-runtime-container-name-missing",
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            AiKubernetesRuntimePodSpec podSpec;

            try
            {
                podSpec = this.podSpecBuilder.Build(request);
            }
            catch (Exception exception)
            {
                return CreateRejectedResult(
                    request,
                    exception.Message,
                    retryable: false,
                    metadata: CreateBaseMetadata());
            }

            var createResult =
                await this.client
                    .CreateRuntimeHostAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            var metadata =
                MergeMetadata(
                    podSpec.Annotations,
                    createResult.Metadata);

            if (!createResult.Success)
            {
                return CreateRejectedResult(
                    request,
                    createResult.FailureReason ?? "kubernetes-runtime-host-create-failed",
                    createResult.Retryable,
                    metadata);
            }

            var hostReadinessResult =
                await this.client
                    .WaitUntilHostReadyAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

            metadata =
                MergeMetadata(
                    metadata,
                    hostReadinessResult.Metadata);

            if (!hostReadinessResult.Success)
            {
                await this.DeleteOnFailureAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

                return CreateRejectedResult(
                    request,
                    hostReadinessResult.FailureReason ?? "kubernetes-runtime-host-readiness-failed",
                    hostReadinessResult.Retryable,
                    metadata);
            }

            if (!this.options.RequireRuntimeReadiness)
            {
                return AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    metadata);
            }

            var runtimeReadinessResult =
                await this.readinessWaiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            ControlPlaneId = request.ControlPlaneId,
                            ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                            RuntimeInstanceId = request.RuntimeInstanceId,
                            ProviderName = request.ProviderName,
                            TransportName = request.TransportName,
                            TransportEndpoint = request.TransportEndpoint,
                            RequireTransportEndpoint = true,
                            Timeout = this.options.ReadinessTimeout
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!runtimeReadinessResult.Success)
            {
                await this.DeleteOnFailureAsync(
                        podSpec,
                        cancellationToken)
                    .ConfigureAwait(false);

                return CreateRejectedResult(
                    request,
                    runtimeReadinessResult.FailureReason ?? "kubernetes-runtime-readiness-failed",
                    retryable: runtimeReadinessResult.TimedOut,
                    metadata: metadata);
            }

            return AiRuntimeHostStartResult.Started(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                runtimeReadinessResult.TransportEndpoint ?? request.TransportEndpoint,
                metadata);
        }

        /// <summary>
        /// Deletes Kubernetes resources after a failed host creation flow when configured to do so.
        /// </summary>
        /// <param name="podSpec">The Kubernetes runtime pod specification.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous operation.</returns>
        private async Task DeleteOnFailureAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken)
        {
            if (!this.options.DeleteResourcesOnFailure)
            {
                return;
            }

            await this.client
                .DeleteRuntimeHostAsync(
                    podSpec,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a rejected runtime host start result.
        /// </summary>
        /// <param name="request">The runtime host start request.</param>
        /// <param name="failureReason">The structured failure reason.</param>
        /// <param name="retryable">A value indicating whether the failure is retryable.</param>
        /// <param name="metadata">The result metadata.</param>
        /// <returns>The rejected runtime host start result.</returns>
        private static AiRuntimeHostStartResult CreateRejectedResult(
            AiRuntimeHostStartRequest request,
            string failureReason,
            bool retryable,
            IReadOnlyDictionary<string, string> metadata)
        {
            return AiRuntimeHostStartResult.Rejected(
                request.ExecutionContextSnapshot,
                request.RuntimeInstanceId,
                request.ProviderName,
                request.TransportName,
                request.TransportEndpoint,
                failureReason,
                retryable,
                metadata);
        }

        /// <summary>
        /// Creates base Kubernetes host lifecycle metadata.
        /// </summary>
        /// <returns>The base metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateBaseMetadata()
        {
            return new Dictionary<string, string>
            {
                [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                [AiRuntimeHostMetadataKeys.HostCreationStrategy] = nameof(KubernetesAiRuntimeHostCreationStrategy)
            };
        }

        /// <summary>
        /// Merges metadata dictionaries using case-insensitive keys.
        /// </summary>
        /// <param name="first">The first metadata dictionary.</param>
        /// <param name="second">The second metadata dictionary.</param>
        /// <returns>The merged metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> MergeMetadata(
            IReadOnlyDictionary<string, string>? first,
            IReadOnlyDictionary<string, string>? second)
        {
            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in CreateBaseMetadata())
            {
                metadata[item.Key] = item.Value;
            }

            if (first is not null)
            {
                foreach (var item in first)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            if (second is not null)
            {
                foreach (var item in second)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            return metadata;
        }
    }
}