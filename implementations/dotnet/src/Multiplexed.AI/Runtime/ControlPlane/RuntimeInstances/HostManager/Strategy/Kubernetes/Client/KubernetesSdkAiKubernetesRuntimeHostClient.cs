using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly AiKubernetesRuntimeHostOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubernetesSdkAiKubernetesRuntimeHostClient"/> class.
        /// </summary>
        /// <param name="clientFactory">The Kubernetes SDK client factory.</param>
        /// <param name="options">The Kubernetes runtime host options.</param>
        public KubernetesSdkAiKubernetesRuntimeHostClient(
            IKubernetesClientFactory clientFactory,
            IOptions<AiKubernetesRuntimeHostOptions> options)
        {
            this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
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
                using var client = this.clientFactory.CreateClient();

                var pod = CreatePod(podSpec);

                await client.CoreV1.CreateNamespacedPodAsync(
                    pod,
                    podSpec.Namespace,
                    cancellationToken: cancellationToken);

                string? serviceName = null;

                if (this.options.UseServicePerRuntime)
                {
                    var service = CreateService(podSpec);

                    await client.CoreV1.CreateNamespacedServiceAsync(
                        service,
                        podSpec.Namespace,
                        cancellationToken: cancellationToken);

                    serviceName = service.Metadata.Name;
                }

                return AiKubernetesRuntimeHostCreateResult.Created(
                    podSpec.Namespace,
                    podSpec.PodName,
                    serviceName,
                    CreateMetadata(podSpec, serviceName));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostCreateResult.Rejected(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    metadata: CreateMetadata(podSpec, serviceName: null));
            }
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostReadinessResult> WaitUntilHostReadyAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            using var client = this.clientFactory.CreateClient();

            var deadline = DateTimeOffset.UtcNow.Add(this.options.StartupTimeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var pod =
                        await client.CoreV1.ReadNamespacedPodStatusAsync(
                            podSpec.PodName,
                            podSpec.Namespace,
                            cancellationToken: cancellationToken);

                    if (IsPodReady(pod))
                    {
                        var serviceName = this.options.UseServicePerRuntime ? CreateServiceName(podSpec) : null;

                        return AiKubernetesRuntimeHostReadinessResult.Ready(
                            podSpec.Namespace,
                            podSpec.PodName,
                            serviceName,
                            CreateMetadata(podSpec, serviceName));
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
                        metadata: CreateMetadata(podSpec, serviceName: null));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            return AiKubernetesRuntimeHostReadinessResult.Failed(
                podSpec.Namespace,
                podSpec.PodName,
                "kubernetes-runtime-host-readiness-timeout",
                timedOut: true,
                retryable: true,
                serviceName: this.options.UseServicePerRuntime ? CreateServiceName(podSpec) : null,
                metadata: CreateMetadata(podSpec, this.options.UseServicePerRuntime ? CreateServiceName(podSpec) : null));
        }

        /// <inheritdoc />
        public async Task<AiKubernetesRuntimeHostDeleteResult> DeleteRuntimeHostAsync(
            AiKubernetesRuntimePodSpec podSpec,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            try
            {
                using var client = this.clientFactory.CreateClient();

                string? serviceName = null;

                if (this.options.UseServicePerRuntime)
                {
                    serviceName = CreateServiceName(podSpec);

                    await client.CoreV1.DeleteNamespacedServiceAsync(
                        serviceName,
                        podSpec.Namespace,
                        cancellationToken: cancellationToken);
                }

                await client.CoreV1.DeleteNamespacedPodAsync(
                    podSpec.PodName,
                    podSpec.Namespace,
                    cancellationToken: cancellationToken);

                return AiKubernetesRuntimeHostDeleteResult.Deleted(
                    podSpec.Namespace,
                    podSpec.PodName,
                    serviceName,
                    CreateMetadata(podSpec, serviceName));
            }
            catch (Exception exception)
            {
                return AiKubernetesRuntimeHostDeleteResult.Failed(
                    podSpec.Namespace,
                    podSpec.PodName,
                    exception.Message,
                    retryable: true,
                    metadata: CreateMetadata(podSpec, serviceName: null));
            }
        }

        private static V1Pod CreatePod(AiKubernetesRuntimePodSpec podSpec)
        {
            return new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = podSpec.PodName,
                    NamespaceProperty = podSpec.Namespace,
                    Labels = new Dictionary<string, string>(podSpec.Labels),
                    Annotations = new Dictionary<string, string>(podSpec.Annotations)
                },
                Spec = new V1PodSpec
                {
                    ServiceAccountName = podSpec.ServiceAccountName,
                    Containers =
                        new List<V1Container>
                        {
                            new()
                            {
                                Name = podSpec.ContainerName,
                                Image = podSpec.RuntimeImage,
                                Ports =
                                    new List<V1ContainerPort>
                                    {
                                        new()
                                        {
                                            ContainerPort = podSpec.ContainerPort
                                        }
                                    },
                                Env =
                                    podSpec.EnvironmentVariables
                                        .Select(pair =>
                                            new V1EnvVar
                                            {
                                                Name = pair.Key,
                                                Value = pair.Value
                                            })
                                        .ToList()
                            }
                        },
                    RestartPolicy = "Never"
                }
            };
        }

        private static V1Service CreateService(AiKubernetesRuntimePodSpec podSpec)
        {
            var serviceName = CreateServiceName(podSpec);

            return new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = serviceName,
                    NamespaceProperty = podSpec.Namespace,
                    Labels = new Dictionary<string, string>(podSpec.Labels),
                    Annotations = new Dictionary<string, string>(podSpec.Annotations)
                },
                Spec = new V1ServiceSpec
                {
                    Type = "ClusterIP",
                    Selector = new Dictionary<string, string>
                    {
                        ["multiplexed.ai/runtime-instance-id"] = podSpec.Labels["multiplexed.ai/runtime-instance-id"]
                    },
                    Ports =
                        new List<V1ServicePort>
                        {
                            new()
                            {
                                Name = podSpec.ContainerName,
                                Port = podSpec.ContainerPort,
                                TargetPort = podSpec.ContainerPort
                            }
                        }
                }
            };
        }

        private static bool IsPodReady(V1Pod pod)
        {
            return pod.Status?.Conditions?.Any(
                condition =>
                    string.Equals(condition.Type, ReadyConditionType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(condition.Status, "True", StringComparison.OrdinalIgnoreCase)) == true;
        }

        private static string CreateServiceName(AiKubernetesRuntimePodSpec podSpec)
        {
            return $"{podSpec.PodName}-svc";
        }

        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiKubernetesRuntimePodSpec podSpec,
            string? serviceName)
        {
            var metadata = new Dictionary<string, string>
            {
                [AiKubernetesRuntimeHostMetadataKeys.Namespace] = podSpec.Namespace,
                [AiKubernetesRuntimeHostMetadataKeys.PodName] = podSpec.PodName,
                [AiKubernetesRuntimeHostMetadataKeys.ContainerName] = podSpec.ContainerName
            };

            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                metadata[AiKubernetesRuntimeHostMetadataKeys.ServiceName] = serviceName;
            }

            return metadata;
        }
    }
}