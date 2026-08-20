using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Options;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;

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

                var podAlreadyExisted = false;
                var serviceAlreadyExisted = false;

                try
                {
                    await client
                        .CreatePodAsync(
                            pod,
                            podSpec.Namespace,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsAlreadyExists(exception))
                {
                    var existingPod =
                        await client
                            .ReadPodStatusAsync(
                                podSpec.PodName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);

                    ValidateExistingPodIdentity(
                        pod,
                        existingPod);

                    podAlreadyExisted = true;
                }

                string? serviceName = null;
                V1Service? createdService = null;

                if (this.options.UseServicePerRuntime)
                {
                    var service =
                        this.resourceFactory.CreateService(podSpec);

                    serviceName = service.Metadata.Name;

                    try
                    {
                        await client
                            .CreateServiceAsync(
                                service,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsAlreadyExists(exception))
                    {
                        var existingService =
                            await client
                                .ReadServiceAsync(
                                    serviceName,
                                    podSpec.Namespace,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        ValidateExistingServiceIdentity(
                            service,
                            existingService);

                        serviceAlreadyExisted = true;
                    }

                    createdService =
                        await client
                            .ReadServiceAsync(
                                serviceName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                }

                var metadata =
                    new System.Collections.Generic.Dictionary<string, string>(
                        this.resourceFactory.CreateMetadata(
                            podSpec,
                            serviceName,
                            createdService),
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["kubernetes.creation.converged"] =
                            (podAlreadyExisted || serviceAlreadyExisted)
                                .ToString(),
                        [AiKubernetesRuntimeHostMetadataKeys.PodAlreadyExists] =
                            podAlreadyExisted.ToString(),
                        [AiKubernetesRuntimeHostMetadataKeys.ServiceAlreadyExists] =
                            serviceAlreadyExisted.ToString()
                    };

                return AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    serviceName,
                    metadata);
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

        private static void ValidateExistingPodIdentity(
            V1Pod expectedPod,
            V1Pod existingPod)
        {
            ValidateExistingResourceIdentity(
                "pod",
                expectedPod.Metadata,
                existingPod.Metadata);
        }

        private static void ValidateExistingServiceIdentity(
            V1Service expectedService,
            V1Service existingService)
        {
            ValidateExistingResourceIdentity(
                "service",
                expectedService.Metadata,
                existingService.Metadata);
        }

        private static void ValidateExistingResourceIdentity(
            string resourceKind,
            V1ObjectMeta? expectedMetadata,
            V1ObjectMeta? existingMetadata)
        {
            var expectedName =
                expectedMetadata?.Name ?? string.Empty;

            var actualName =
                existingMetadata?.Name ?? string.Empty;

            if (!string.Equals(
                    actualName,
                    expectedName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Kubernetes {resourceKind} identity collision detected. ExpectedName='{expectedName}', ActualName='{actualName}'.");
            }

            if (expectedMetadata?.Labels is null)
            {
                return;
            }

            foreach (var expectedLabel in expectedMetadata.Labels)
            {
                string? actualValue = null;

                var labelExists =
                    existingMetadata?.Labels is not null &&
                    existingMetadata.Labels.TryGetValue(
                        expectedLabel.Key,
                        out actualValue);

                if (!labelExists ||
                    !string.Equals(
                        actualValue,
                        expectedLabel.Value,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Kubernetes {resourceKind} identity collision detected. ResourceName='{expectedName}', Label='{expectedLabel.Key}', ExpectedValue='{expectedLabel.Value}', ActualValue='{actualValue ?? "(missing)"}'.");
                }
            }
        }

        private static bool IsPodReady(
            V1Pod pod)
        {
            return pod.Status?.Conditions?.Any(
                condition =>
                    string.Equals(
                        condition.Type,
                        AiKubernetesRuntimeConditionTypes.Ready,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        condition.Status,
                        "True",
                        StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static bool IsAlreadyExists(
            Exception exception)
        {
            return exception is HttpOperationException httpOperationException &&
                httpOperationException.Response.StatusCode == HttpStatusCode.Conflict;
        }

        private static bool IsNotFound(
            Exception exception)
        {
            return exception is HttpOperationException httpOperationException &&
                httpOperationException.Response.StatusCode == HttpStatusCode.NotFound;
        }
    }
}