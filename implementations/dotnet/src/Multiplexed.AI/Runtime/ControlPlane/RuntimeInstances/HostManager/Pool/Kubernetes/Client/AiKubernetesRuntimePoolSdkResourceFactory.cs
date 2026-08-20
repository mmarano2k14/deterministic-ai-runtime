using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using k8s.Models;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Pool;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client
{
    /// <summary>
    /// Creates Kubernetes SDK resources for one opt-in Runtime Pool Pod and stable Service.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolSdkResourceFactory
    {
        private const int KubernetesDnsLabelMaximumLength = 63;

        private const string KubernetesNamespaceEnvironmentVariable =
            "AiKubernetesRuntimePoolInPod__KubernetesNamespace";

        private const string KubernetesPodNameEnvironmentVariable =
            "AiKubernetesRuntimePoolInPod__KubernetesPodName";

        private const string KubernetesNodeNameEnvironmentVariable =
            "AiKubernetesRuntimePoolInPod__KubernetesNodeName";

        private readonly AiKubernetesRuntimePoolHostOptions options;

        /// <summary>
        /// Initializes a new Kubernetes Runtime Pool resource factory.
        /// </summary>
        public AiKubernetesRuntimePoolSdkResourceFactory(
            AiKubernetesRuntimePoolHostOptions options)
        {
            this.options =
                options
                ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Creates one Pod containing the Runtime Pool host container and all declared ports.
        /// </summary>
        public V1Pod CreatePod(
            AiKubernetesRuntimePoolPodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            return new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = podSpec.PodName,
                    NamespaceProperty = podSpec.Namespace,
                    Labels =
                        new Dictionary<string, string>(
                            podSpec.Labels),
                    Annotations =
                        new Dictionary<string, string>(
                            podSpec.Annotations)
                },
                Spec = new V1PodSpec
                {
                    ServiceAccountName = podSpec.ServiceAccountName,
                    RestartPolicy = "Never",
                    Volumes =
                        new List<V1Volume>
                        {
                            new()
                            {
                                Name = "pod-identity",
                                DownwardAPI =
                                    new V1DownwardAPIVolumeSource
                                    {
                                        Items =
                                            new List<
                                                V1DownwardAPIVolumeFile>
                                            {
                                                new()
                                                {
                                                    Path = "uid",
                                                    FieldRef =
                                                        new V1ObjectFieldSelector
                                                        {
                                                            FieldPath =
                                                                "metadata.uid"
                                                        }
                                                }
                                            }
                                    }
                            }
                        },
                    Containers =
                        new List<V1Container>
                        {
                            new()
                            {
                                Name = podSpec.ContainerName,
                                Image = podSpec.RuntimeImage,
                                ImagePullPolicy =
                                    podSpec.ImagePullPolicy.ToString(),
                                Args =
                                    podSpec.ContainerArguments.ToList(),
                                Env =
                                    new List<V1EnvVar>
                                    {
                                        CreateDownwardApiEnvironmentVariable(
                                            KubernetesNamespaceEnvironmentVariable,
                                            "metadata.namespace"),
                                        CreateDownwardApiEnvironmentVariable(
                                            KubernetesPodNameEnvironmentVariable,
                                            "metadata.name"),
                                        CreateDownwardApiEnvironmentVariable(
                                            KubernetesNodeNameEnvironmentVariable,
                                            "spec.nodeName")
                                    },
                                Ports =
                                    podSpec.Ports
                                        .Select(port =>
                                            new V1ContainerPort
                                            {
                                                Name = port.Name,
                                                ContainerPort = port.Port,
                                                Protocol = "TCP"
                                            })
                                        .ToList(),
                                VolumeMounts =
                                    new List<V1VolumeMount>
                                    {
                                        new()
                                        {
                                            Name = "pod-identity",
                                            MountPath =
                                                this.options.PodIdentityMountPath,
                                            ReadOnlyProperty = true
                                        }
                                    },
                                ReadinessProbe =
                                    new V1Probe
                                    {
                                        HttpGet =
                                            new V1HTTPGetAction
                                            {
                                                Path =
                                                    "/runtime-pool/readiness",
                                                Port =
                                                    podSpec
                                                        .Bootstrap
                                                        .ReadinessPort,
                                                Scheme = "HTTP"
                                            },
                                        PeriodSeconds = 1,
                                        TimeoutSeconds = 1,
                                        FailureThreshold = 90
                                    }
                            }
                        }
                }
            };
        }

        /// <summary>
        /// Creates one strongly typed Pod identity environment variable from the Kubernetes
        /// Downward API.
        /// </summary>
        private static V1EnvVar CreateDownwardApiEnvironmentVariable(
            string name,
            string fieldPath)
        {
            return new V1EnvVar
            {
                Name = name,
                ValueFrom =
                    new V1EnvVarSource
                    {
                        FieldRef =
                            new V1ObjectFieldSelector
                            {
                                FieldPath = fieldPath
                            }
                    }
            };
        }

        /// <summary>
        /// Creates one stable Service that targets only the exact planned Pod.
        /// </summary>
        public V1Service CreateService(
            AiKubernetesRuntimePoolPodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            var stablePort = podSpec.Ports[0];

            return new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = this.CreateServiceName(podSpec),
                    NamespaceProperty = podSpec.Namespace,
                    Labels =
                        new Dictionary<string, string>(
                            podSpec.Labels),
                    Annotations =
                        new Dictionary<string, string>(
                            podSpec.Annotations)
                },
                Spec = new V1ServiceSpec
                {
                    Type = this.options.ServiceType,
                    Selector =
                        new Dictionary<string, string>
                        {
                            ["app.kubernetes.io/instance"] =
                                podSpec.Labels["app.kubernetes.io/instance"]
                        },
                    Ports =
                        new List<V1ServicePort>
                        {
                            new()
                            {
                                Name = stablePort.Name,
                                Port = stablePort.Port,
                                TargetPort = stablePort.Port,
                                Protocol = "TCP",
                                AppProtocol =
                                    string.Equals(
                                        podSpec.Bootstrap.TransportName,
                                        "grpc",
                                        StringComparison.OrdinalIgnoreCase)
                                        ? "kubernetes.io/h2c"
                                        : "http"
                            }
                        }
                }
            };
        }

        /// <summary>
        /// Creates the stable Service name.
        /// </summary>
        public string CreateServiceName(
            AiKubernetesRuntimePoolPodSpec podSpec)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            return CreateKubernetesDnsLabel(
                string.Concat(
                    podSpec.PodName,
                    "-svc"));
        }

        /// <summary>
        /// Creates lifecycle metadata for the created Runtime Pool resources.
        /// </summary>
        public IReadOnlyDictionary<string, string> CreateMetadata(
            AiKubernetesRuntimePoolPodSpec podSpec,
            V1Pod? pod,
            V1Service? service)
        {
            ArgumentNullException.ThrowIfNull(podSpec);

            var serviceName =
                service?.Metadata?.Name
                ?? (this.options.CreateService
                    ? this.CreateServiceName(podSpec)
                    : null);

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiKubernetesRuntimeHostMetadataKeys.Namespace] =
                        podSpec.Namespace,
                    [AiKubernetesRuntimeHostMetadataKeys.PodName] =
                        podSpec.PodName,
                    [AiKubernetesRuntimeHostMetadataKeys.ContainerName] =
                        podSpec.ContainerName,
                    [AiRuntimePoolMetadataKeys.PoolId] = podSpec.PoolId,
                    [AiRuntimePoolMetadataKeys.PodRequestId] =
                        podSpec.PodRequestId,
                    [AiRuntimePoolMetadataKeys.PlannedRuntimeCount] =
                        podSpec.Bootstrap.RuntimeInstances.Count.ToString(),
                    [AiRuntimePoolMetadataKeys.PlannedRuntimeInstanceIds] =
                        string.Join(
                            ",",
                            podSpec.Bootstrap.RuntimeInstances
                                .Select(runtime =>
                                    runtime.RuntimeInstanceId))
                };

            var podUid = pod?.Metadata?.Uid;
            if (!string.IsNullOrWhiteSpace(podUid))
            {
                metadata[AiKubernetesRuntimeHostMetadataKeys.PodUid] = podUid;
                metadata[AiRuntimeHostMetadataKeys.HostId] = podUid;
            }

            if (!string.IsNullOrWhiteSpace(serviceName))
            {
                var serviceDnsName =
                    string.Concat(
                        serviceName,
                        ".",
                        podSpec.Namespace,
                        ".svc.cluster.local");

                var serviceEndpoint =
                    string.Concat(
                        "http://",
                        serviceDnsName,
                        ":",
                        podSpec.Bootstrap.StableTransportPort);

                metadata[AiKubernetesRuntimeHostMetadataKeys.ServiceName] =
                    serviceName;
                metadata[AiKubernetesRuntimeHostMetadataKeys.ServiceDns] = serviceDnsName;
                metadata[AiKubernetesRuntimeHostMetadataKeys.ServiceEndpoint] =
                    serviceEndpoint;
                metadata[AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] = serviceEndpoint;
                metadata[
                    AiRuntimeInstanceCommandTransportMetadataKeys
                        .TransportEndpoint] = serviceEndpoint;

                var nodePort =
                    service?
                        .Spec?
                        .Ports?
                        .FirstOrDefault()?
                        .NodePort;

                if (string.Equals(
                        this.options.ServiceType,
                        "NodePort",
                        StringComparison.OrdinalIgnoreCase)
                    && nodePort.HasValue
                    && nodePort.Value > 0)
                {
                    var nodePortEndpoint =
                        string.Concat(
                            "http://",
                            this.options.NodePortHost,
                            ":",
                            nodePort.Value);

                    metadata[AiKubernetesRuntimeHostMetadataKeys.NodePort] =
                        nodePort.Value.ToString();
                    metadata[AiKubernetesRuntimeHostMetadataKeys.NodePortHost] =
                        this.options.NodePortHost;
                    metadata[AiKubernetesRuntimeHostMetadataKeys.NodePortEndpoint] =
                        nodePortEndpoint;
                    metadata[AiRuntimeInstanceCommandTransportMetadataKeys.CamelCaseTransportEndpoint] =
                        nodePortEndpoint;
                    metadata[
                        AiRuntimeInstanceCommandTransportMetadataKeys
                            .TransportEndpoint] = nodePortEndpoint;
                }
            }

            return metadata;
        }

        /// <summary>
        /// Creates a DNS-label-compatible Kubernetes resource name.
        /// </summary>
        private static string CreateKubernetesDnsLabel(
            string rawName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawName);

            var normalized =
                new string(
                    rawName
                        .ToLowerInvariant()
                        .Select(character =>
                            char.IsLetterOrDigit(character)
                            || character == '-'
                                ? character
                                : '-')
                        .ToArray());

            normalized =
                string.Join(
                    "-",
                    normalized.Split(
                        '-',
                        StringSplitOptions.RemoveEmptyEntries))
                    .Trim('-');

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "runtime-pool";
            }

            if (normalized.Length <= KubernetesDnsLabelMaximumLength)
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
                KubernetesDnsLabelMaximumLength
                - hash.Length
                - 1;

            var prefix =
                normalized[..prefixLength]
                    .TrimEnd('-');

            return string.IsNullOrWhiteSpace(prefix)
                ? hash
                : string.Concat(
                    prefix,
                    "-",
                    hash);
        }
    }
}
