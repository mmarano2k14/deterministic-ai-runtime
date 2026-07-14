using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using System.Collections.Generic;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="AiKubernetesSdkResourceFactory"/>.
    /// </summary>
    public sealed class AiKubernetesSdkResourceFactoryTests
    {
        /// <summary>
        /// Verifies that a Kubernetes pod is created from the runtime pod specification.
        /// </summary>
        [Fact]
        public void CreatePod_Should_Create_Kubernetes_Pod_From_Runtime_Pod_Spec()
        {
            var factory =
                new AiKubernetesSdkResourceFactory(
                    Options.Create(
                        new AiKubernetesRuntimeHostOptions()));

            var podSpec = CreatePodSpec();

            var pod = factory.CreatePod(podSpec);

            Assert.Equal("runtime-tenant-a-001", pod.Metadata.Name);
            Assert.Equal("ai-runtime", pod.Metadata.NamespaceProperty);
            Assert.Equal("tenant-a-runtime-001", pod.Metadata.Labels["multiplexed.ai/runtime-instance-id"]);
            Assert.Equal("kubernetes", pod.Metadata.Annotations[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal("Never", pod.Spec.RestartPolicy);
            Assert.Equal("runtime-instance", pod.Spec.Containers[0].Name);
            Assert.Equal("multiplexed-ai-runtime:test", pod.Spec.Containers[0].Image);
            Assert.Equal(8080, pod.Spec.Containers[0].Ports[0].ContainerPort);
            Assert.Contains(pod.Spec.Containers[0].Env, env => env.Name == "AiMcpHost__Mode" && env.Value == "RuntimeInstanceOnly");
        }

        /// <summary>
        /// Verifies that a Kubernetes service is created from the runtime pod specification.
        /// </summary>
        [Fact]
        public void CreateService_Should_Create_ClusterIp_Service_For_Runtime_Pod()
        {
            var factory =
                new AiKubernetesSdkResourceFactory(
                    Options.Create(
                        new AiKubernetesRuntimeHostOptions()));

            var podSpec = CreatePodSpec();

            var service = factory.CreateService(podSpec);

            Assert.Equal("runtime-tenant-a-001-svc", service.Metadata.Name);
            Assert.Equal("ai-runtime", service.Metadata.NamespaceProperty);
            Assert.Equal("NodePort", service.Spec.Type);
            Assert.Equal("tenant-a-runtime-001", service.Spec.Selector["multiplexed.ai/runtime-instance-id"]);
            Assert.Equal("runtime-instance", service.Spec.Ports[0].Name);
            Assert.Equal(8080, service.Spec.Ports[0].Port);
            Assert.Equal("8080", service.Spec.Ports[0].TargetPort.Value);
        }

        /// <summary>
        /// Verifies that service names are deterministic.
        /// </summary>
        [Fact]
        public void CreateServiceName_Should_Create_Deterministic_Service_Name()
        {
            var factory =
                new AiKubernetesSdkResourceFactory(
                    Options.Create(
                        new AiKubernetesRuntimeHostOptions()));

            var podSpec = CreatePodSpec();

            var serviceName = factory.CreateServiceName(podSpec);

            Assert.Equal("runtime-tenant-a-001-svc", serviceName);
        }

        /// <summary>
        /// Verifies that lifecycle metadata is created from the runtime pod specification.
        /// </summary>
        [Fact]
        public void CreateMetadata_Should_Create_Runtime_Host_Metadata()
        {
            var factory =
                new AiKubernetesSdkResourceFactory(
                    Options.Create(
                        new AiKubernetesRuntimeHostOptions()));

            var podSpec = CreatePodSpec();

            var metadata = factory.CreateMetadata(podSpec, "runtime-tenant-a-001-svc");

            Assert.Equal("ai-runtime", metadata[AiKubernetesRuntimeHostMetadataKeys.Namespace]);
            Assert.Equal("runtime-tenant-a-001", metadata[AiKubernetesRuntimeHostMetadataKeys.PodName]);
            Assert.Equal("runtime-instance", metadata[AiKubernetesRuntimeHostMetadataKeys.ContainerName]);
            Assert.Equal("runtime-tenant-a-001-svc", metadata[AiKubernetesRuntimeHostMetadataKeys.ServiceName]);
        }

        /// <summary>
        /// Verifies that the Kubernetes image pull policy is mapped to the pod container.
        /// </summary>
        [Fact]
        public void CreatePod_Should_Map_Image_Pull_Policy_To_Container()
        {
            var factory =
                new AiKubernetesSdkResourceFactory(
                    Options.Create(
                        new AiKubernetesRuntimeHostOptions()));

            var podSpec = CreatePodSpec(AiKubernetesImagePullPolicy.Never);

            var pod = factory.CreatePod(podSpec);

            Assert.Equal("Never", pod.Spec.Containers[0].ImagePullPolicy);
        }

        private static AiKubernetesRuntimePodSpec CreatePodSpec(
            AiKubernetesImagePullPolicy imagePullPolicy = AiKubernetesImagePullPolicy.IfNotPresent)
        {
            return new AiKubernetesRuntimePodSpec
            {
                Namespace = "ai-runtime",
                PodName = "runtime-tenant-a-001",
                RuntimeImage = "multiplexed-ai-runtime:test",
                ImagePullPolicy = imagePullPolicy,
                ContainerName = "runtime-instance",
                ContainerPort = 8080,
                ServiceAccountName = "runtime-service-account",
                Labels =
                    new Dictionary<string, string>
                    {
                        ["multiplexed.ai/runtime-instance-id"] = "tenant-a-runtime-001",
                        ["multiplexed.ai/provider"] = "grpc",
                        ["multiplexed.ai/transport"] = "grpc"
                    },
                Annotations =
                    new Dictionary<string, string>
                    {
                        [AiRuntimeHostMetadataKeys.HostProvider] = "kubernetes",
                        [AiRuntimeHostMetadataKeys.HostCreationMode] = "Kubernetes",
                        [AiRuntimeHostMetadataKeys.HostCreationStrategy] = "KubernetesAiRuntimeHostCreationStrategy"
                    },
                EnvironmentVariables =
                    new Dictionary<string, string>
                    {
                        ["AiMcpHost__Mode"] = "RuntimeInstanceOnly",
                        ["AiRuntimeInstanceRegistration__RuntimeInstanceId"] = "tenant-a-runtime-001",
                        ["AiRuntimeInstanceRegistration__ProviderName"] = "grpc",
                        ["AiRuntimeInstanceRegistration__TransportName"] = "grpc"
                    }
            };
        }
    }
}