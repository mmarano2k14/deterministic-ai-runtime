using k8s.Models;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client
{
    /// <summary>
    /// Creates Kubernetes SDK resource models for runtime host lifecycle operations.
    /// </summary>
    /// <remarks>
    /// This factory only builds Kubernetes host resources.
    /// Runtime command transport remains HTTP or gRPC and is validated separately by runtime readiness.
    /// </remarks>
    public sealed class AiKubernetesSdkResourceFactory
    {
        private const int KubernetesDnsLabelMaxLength = 63;

        /// <summary>
        /// Creates a Kubernetes pod for a runtime instance.
        /// </summary>
        /// <param name="podSpec">The runtime pod specification.</param>
        /// <returns>The Kubernetes pod model.</returns>
        public V1Pod CreatePod(
            AiKubernetesRuntimePodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

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
                                ImagePullPolicy = podSpec.ImagePullPolicy.ToString(),
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

        /// <summary>
        /// Creates a Kubernetes service for a runtime instance.
        /// </summary>
        /// <param name="podSpec">The runtime pod specification.</param>
        /// <returns>The Kubernetes service model.</returns>
        public V1Service CreateService(
            AiKubernetesRuntimePodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            var serviceName =
                this.CreateServiceName(
                    podSpec);

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

        /// <summary>
        /// Creates the Kubernetes service name for a runtime instance pod.
        /// </summary>
        /// <param name="podSpec">The runtime pod specification.</param>
        /// <returns>The Kubernetes service name.</returns>
        public string CreateServiceName(
            AiKubernetesRuntimePodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            return CreateKubernetesDnsLabel(
                $"{podSpec.PodName}-svc");
        }

        /// <summary>
        /// Creates Kubernetes runtime host metadata returned by lifecycle operations.
        /// </summary>
        /// <param name="podSpec">The runtime pod specification.</param>
        /// <param name="serviceName">The optional Kubernetes service name.</param>
        /// <returns>The lifecycle metadata.</returns>
        public IReadOnlyDictionary<string, string> CreateMetadata(
            AiKubernetesRuntimePodSpec podSpec,
            string? serviceName)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

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

        /// <summary>
        /// Creates a Kubernetes DNS label compatible resource name.
        /// </summary>
        /// <param name="rawName">The raw resource name.</param>
        /// <returns>The normalized Kubernetes DNS label.</returns>
        private static string CreateKubernetesDnsLabel(
            string rawName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawName);

            var normalized =
                new string(
                    rawName
                        .ToLowerInvariant()
                        .Select(character =>
                            char.IsLetterOrDigit(character) || character == '-'
                                ? character
                                : '-')
                        .ToArray());

            normalized =
                string.Join(
                    "-",
                    normalized.Split(
                        '-',
                        StringSplitOptions.RemoveEmptyEntries));

            normalized =
                normalized.Trim('-');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "runtime";
            }

            if (normalized.Length <= KubernetesDnsLabelMaxLength)
            {
                return normalized;
            }

            var hash =
                Convert
                    .ToHexString(
                        SHA256.HashData(
                            Encoding.UTF8.GetBytes(normalized)))
                    .ToLowerInvariant()[..10];

            var prefixLength =
                KubernetesDnsLabelMaxLength - hash.Length - 1;

            var prefix =
                normalized[..prefixLength].TrimEnd('-');

            if (string.IsNullOrWhiteSpace(prefix))
            {
                return hash;
            }

            return $"{prefix}-{hash}";
        }
    }
}