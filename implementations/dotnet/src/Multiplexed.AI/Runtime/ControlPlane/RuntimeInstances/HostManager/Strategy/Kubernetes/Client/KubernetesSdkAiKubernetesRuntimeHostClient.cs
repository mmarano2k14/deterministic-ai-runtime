using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Provides a Kubernetes SDK backed runtime host lifecycle client.
    /// </summary>
    /// <remarks>
    /// This client creates Kubernetes host capacity only.
    /// Runtime command transport remains HTTP or gRPC and is validated separately by runtime readiness.
    /// </remarks>
    public sealed class KubernetesSdkAiKubernetesRuntimeHostClient : IAiKubernetesRuntimeHostClient
    {
        private const string ReadyConditionType = "Ready";

        private readonly IKubernetesClientFactory clientFactory;
        private readonly AiKubernetesSdkResourceFactory resourceFactory;
        private readonly AiKubernetesRuntimeHostOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesSdkAiKubernetesRuntimeHostClient"/> class.
        /// </summary>
        /// <param name="clientFactory">The Kubernetes SDK client factory.</param>
        /// <param name="resourceFactory">The Kubernetes SDK resource factory.</param>
        /// <param name="options">The Kubernetes runtime host options.</param>
        public KubernetesSdkAiKubernetesRuntimeHostClient(
            IKubernetesClientFactory clientFactory,
            AiKubernetesSdkResourceFactory resourceFactory,
            IOptions<AiKubernetesRuntimeHostOptions> options)
        {
            this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            this.resourceFactory = resourceFactory ?? throw new ArgumentNullException(nameof(resourceFactory));
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostCreateResult> CreateRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            try
            {
                var client =
                    this.clientFactory.CreateClient();

                var pod =
                    this.resourceFactory.CreatePod(podSpec);

                await client
                    .CreatePodAsync(
                        pod,
                        podSpec.Namespace,
                        cancellationToken)
                    .ConfigureAwait(false);

                string? serviceName = null;
                V1Service? createdService = null;

                if (this.options.UseServicePerRuntime)
                {
                    var service =
                        this.resourceFactory.CreateService(podSpec);

                    await client
                        .CreateServiceAsync(
                            service,
                            podSpec.Namespace,
                            cancellationToken)
                        .ConfigureAwait(false);

                    serviceName = service.Metadata.Name;

                    createdService =
                        await client
                            .ReadServiceAsync(
                                serviceName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                }

                return AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    serviceName,
                    this.resourceFactory.CreateMetadata(
                        podSpec,
                        serviceName,
                        createdService));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostCreateResult.Rejected(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    metadata: this.resourceFactory.CreateMetadata(
                        podSpec,
                        serviceName: null));
            }
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostReadinessResult> WaitUntilHostReadyAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            var client =
                this.clientFactory.CreateClient();

            var deadline =
                DateTimeOffset.UtcNow.Add(this.options.StartupTimeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var pod =
                        await client
                            .ReadPodStatusAsync(
                                podSpec.PodName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (IsPodReady(pod))
                    {
                        var serviceName =
                            this.options.UseServicePerRuntime
                                ? this.resourceFactory.CreateServiceName(podSpec)
                                : null;

                        V1Service? service = null;

                        if (!string.IsNullOrWhiteSpace(serviceName))
                        {
                            service =
                                await client
                                    .ReadServiceAsync(
                                        serviceName,
                                        podSpec.Namespace,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }

                        return AiKubernetesRuntimeHostReadinessResult.Ready(
                            podSpec.Namespace,
                            podSpec.PodName,
                            serviceName,
                            this.resourceFactory.CreateMetadata(
                                podSpec,
                                serviceName,
                                service));
                    }
                }
                catch (Exception exception)
                {
                    return AiKubernetesRuntimeHostReadinessResult.Failed(
                        podSpec.Namespace,
                        podSpec.PodName,
                        exception.Message,
                        timedOut: false,
                        retryable: true,
                        metadata: this.resourceFactory.CreateMetadata(
                            podSpec,
                            serviceName: null));
                }

                await Task
                    .Delay(
                        this.options.ReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var timedOutServiceName =
                this.options.UseServicePerRuntime
                    ? this.resourceFactory.CreateServiceName(podSpec)
                    : null;

            return AiKubernetesRuntimeHostReadinessResult.Failed(
                podSpec.Namespace,
                podSpec.PodName,
                "kubernetes-runtime-host-readiness-timeout",
                timedOut: true,
                retryable: true,
                serviceName: timedOutServiceName,
                metadata: this.resourceFactory.CreateMetadata(
                    podSpec,
                    timedOutServiceName));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostDeleteResult> DeleteRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            try
            {
                var client =
                    this.clientFactory.CreateClient();

                string? serviceName = null;

                if (this.options.UseServicePerRuntime)
                {
                    serviceName =
                        this.resourceFactory.CreateServiceName(podSpec);

                    try
                    {
                        await client
                            .DeleteServiceAsync(
                                serviceName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsNotFound(exception))
                    {
                    }
                }

                try
                {
                    await client
                        .DeletePodAsync(
                            podSpec.PodName,
                            podSpec.Namespace,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsNotFound(exception))
                {
                }

                return AiKubernetesRuntimeHostDeleteResult.Deleted(
                    podSpec.Namespace,
                    podSpec.PodName,
                    serviceName,
                    this.resourceFactory.CreateMetadata(
                        podSpec,
                        serviceName));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostDeleteResult.Failed(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    metadata: this.resourceFactory.CreateMetadata(
                        podSpec,
                        serviceName: null));
            }
        }

        private static bool IsPodReady(
            V1Pod pod)
        {
            return pod.Status?.Conditions?.Any(
                condition =>
                    string.Equals(
                        condition.Type,
                        ReadyConditionType,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        condition.Status,
                        "True",
                        StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool IsNotFound(
            Exception exception)
        {
            return exception is HttpOperationException httpOperationException &&
                httpOperationException.Response.StatusCode == HttpStatusCode.NotFound;
        }
    }
}