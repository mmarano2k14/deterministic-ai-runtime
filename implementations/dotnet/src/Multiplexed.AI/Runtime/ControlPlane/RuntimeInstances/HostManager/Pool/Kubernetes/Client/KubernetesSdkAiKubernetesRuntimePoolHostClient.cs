using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using k8s.Autorest;
using k8s.Models;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client
{
    /// <summary>
    /// Provides Kubernetes SDK backed lifecycle operations for one Runtime Pool Pod.
    /// </summary>
    public sealed class KubernetesSdkAiKubernetesRuntimePoolHostClient :
        IAiKubernetesRuntimePoolHostClient
    {
        private const string ReadyConditionType = "Ready";

        private readonly IKubernetesClientFactory clientFactory;
        private readonly AiKubernetesRuntimePoolSdkResourceFactory resourceFactory;
        private readonly AiKubernetesRuntimePoolHostOptions options;

        /// <summary>
        /// Initializes a new Kubernetes SDK Runtime Pool lifecycle client.
        /// </summary>
        public KubernetesSdkAiKubernetesRuntimePoolHostClient(
            IKubernetesClientFactory clientFactory,
            AiKubernetesRuntimePoolSdkResourceFactory resourceFactory,
            AiKubernetesRuntimePoolHostOptions options)
        {
            this.clientFactory =
                clientFactory
                ?? throw new ArgumentNullException(nameof(clientFactory));

            this.resourceFactory =
                resourceFactory
                ?? throw new ArgumentNullException(nameof(resourceFactory));

            this.options =
                options
                ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostCreateResult>
            CreateRuntimePoolHostAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            try
            {
                var client = this.clientFactory.CreateClient();
                var desiredPod =
                    this.resourceFactory.CreatePod(podSpec);

                V1Pod actualPod;
                try
                {
                    actualPod =
                        await client
                            .CreatePodAsync(
                                desiredPod,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsAlreadyExists(exception))
                {
                    actualPod =
                        await client
                            .ReadPodStatusAsync(
                                podSpec.PodName,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);

                    ValidateExistingResourceIdentity(
                        "pod",
                        desiredPod.Metadata,
                        actualPod.Metadata);
                }

                V1Service? actualService = null;
                if (this.options.CreateService)
                {
                    var desiredService =
                        this.resourceFactory.CreateService(podSpec);

                    try
                    {
                        actualService =
                            await client
                                .CreateServiceAsync(
                                    desiredService,
                                    podSpec.Namespace,
                                    cancellationToken)
                                .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsAlreadyExists(exception))
                    {
                        actualService =
                            await client
                                .ReadServiceAsync(
                                    desiredService.Metadata.Name,
                                    podSpec.Namespace,
                                    cancellationToken)
                                .ConfigureAwait(false);

                        ValidateExistingResourceIdentity(
                            "service",
                            desiredService.Metadata,
                            actualService.Metadata);
                    }

                    actualService =
                        await client
                            .ReadServiceAsync(
                                desiredService.Metadata.Name,
                                podSpec.Namespace,
                                cancellationToken)
                            .ConfigureAwait(false);
                }

                return AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    actualService?.Metadata?.Name,
                    this.resourceFactory.CreateMetadata(
                        podSpec,
                        actualPod,
                        actualService));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostCreateResult.Rejected(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    metadata:
                        this.resourceFactory.CreateMetadata(
                            podSpec,
                            pod: null,
                            service: null));
            }
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostReadinessResult>
            WaitUntilHostReadyAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            var client = this.clientFactory.CreateClient();
            var deadline =
                DateTimeOffset.UtcNow.Add(
                    this.options.StartupTimeout);

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
                        V1Service? service = null;
                        if (this.options.CreateService)
                        {
                            service =
                                await client
                                    .ReadServiceAsync(
                                        this.resourceFactory
                                            .CreateServiceName(podSpec),
                                        podSpec.Namespace,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                        }

                        return AiKubernetesRuntimeHostReadinessResult.Ready(
                            podSpec.Namespace,
                            podSpec.PodName,
                            service?.Metadata?.Name,
                            this.resourceFactory.CreateMetadata(
                                podSpec,
                                pod,
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
                        serviceName:
                            this.options.CreateService
                                ? this.resourceFactory
                                    .CreateServiceName(podSpec)
                                : null,
                        metadata:
                            this.resourceFactory.CreateMetadata(
                                podSpec,
                                pod: null,
                                service: null));
                }

                await Task
                    .Delay(
                        this.options.ReadinessPollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return AiKubernetesRuntimeHostReadinessResult.Failed(
                podSpec.Namespace,
                podSpec.PodName,
                "kubernetes-runtime-pool-host-readiness-timeout",
                timedOut: true,
                retryable: true,
                serviceName:
                    this.options.CreateService
                        ? this.resourceFactory.CreateServiceName(podSpec)
                        : null,
                metadata:
                    this.resourceFactory.CreateMetadata(
                        podSpec,
                        pod: null,
                        service: null));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostDeleteResult>
            DeleteRuntimePoolHostAsync(
                AiKubernetesRuntimePoolPodSpec podSpec,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            try
            {
                var client = this.clientFactory.CreateClient();
                string? serviceName = null;

                if (this.options.CreateService)
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
                        pod: null,
                        service: null));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostDeleteResult.Failed(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    serviceName:
                        this.options.CreateService
                            ? this.resourceFactory
                                .CreateServiceName(podSpec)
                            : null,
                    metadata:
                        this.resourceFactory.CreateMetadata(
                            podSpec,
                            pod: null,
                            service: null));
            }
        }

        /// <summary>
        /// Validates that an existing converged resource belongs to the same Pod request.
        /// </summary>
        private static void ValidateExistingResourceIdentity(
            string resourceKind,
            V1ObjectMeta? expected,
            V1ObjectMeta? actual)
        {
            if (!string.Equals(
                    expected?.Name,
                    actual?.Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Kubernetes Runtime Pool ",
                        resourceKind,
                        " identity collision."));
            }

            if (expected?.Labels is null)
            {
                return;
            }

            foreach (var expectedLabel in expected.Labels)
            {
                string? actualValue = null;

                var exists =
                    actual?.Labels is not null
                    && actual.Labels.TryGetValue(
                        expectedLabel.Key,
                        out actualValue);

                if (!exists
                    || !string.Equals(
                        expectedLabel.Value,
                        actualValue,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "Kubernetes Runtime Pool ",
                            resourceKind,
                            " identity collision for label '",
                            expectedLabel.Key,
                            "'."));
                }
            }
        }

        /// <summary>
        /// Determines whether Kubernetes reports the Pod as ready.
        /// </summary>
        private static bool IsPodReady(
            V1Pod pod)
        {
            return pod.Status?.Conditions?.Any(
                condition =>
                    string.Equals(
                        condition.Type,
                        ReadyConditionType,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        condition.Status,
                        "True",
                        StringComparison.OrdinalIgnoreCase)) == true;
        }

        /// <summary>
        /// Determines whether Kubernetes reported an existing resource.
        /// </summary>
        private static bool IsAlreadyExists(
            Exception exception)
        {
            return exception is HttpOperationException httpOperationException
                && httpOperationException.Response.StatusCode
                == HttpStatusCode.Conflict;
        }

        /// <summary>
        /// Determines whether Kubernetes reported an absent resource.
        /// </summary>
        private static bool IsNotFound(
            Exception exception)
        {
            return exception is HttpOperationException httpOperationException
                && httpOperationException.Response.StatusCode
                == HttpStatusCode.NotFound;
        }
    }
}
