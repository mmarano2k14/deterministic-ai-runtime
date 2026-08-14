using System;
using System.Linq;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates Kubernetes SDK resource construction for Runtime Pool Pods.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolSdkResourceFactoryTests
    {
        /// <summary>
        /// Verifies that the Pod contains one host container, a dedicated readiness port, and every declared pool port.
        /// </summary>
        [Fact]
        public void CreatePod_Should_Expose_Stable_And_ChildPorts()
        {
            var spec = CreateSpec("http");
            var factory =
                new AiKubernetesRuntimePoolSdkResourceFactory(
                    CreateHostOptions());

            var pod = factory.CreatePod(spec);

            var container = Assert.Single(pod.Spec.Containers);
            Assert.Equal(spec.ContainerName, container.Name);
            Assert.Equal(spec.RuntimeImage, container.Image);
            Assert.Equal("Never", pod.Spec.RestartPolicy);
            Assert.Equal(
                "/runtime-pool/readiness",
                container.ReadinessProbe.HttpGet.Path);
            Assert.Equal(
                spec.Ports.Select(port => port.Port).ToArray(),
                container.Ports
                    .Select(port => port.ContainerPort)
                    .ToArray());

            var environment =
                container.Env.ToDictionary(
                    item => item.Name,
                    StringComparer.Ordinal);

            Assert.Equal(
                "metadata.namespace",
                environment[
                    "AiKubernetesRuntimePoolInPod__KubernetesNamespace"]
                    .ValueFrom
                    .FieldRef
                    .FieldPath);
            Assert.Equal(
                "metadata.name",
                environment[
                    "AiKubernetesRuntimePoolInPod__KubernetesPodName"]
                    .ValueFrom
                    .FieldRef
                    .FieldPath);
            Assert.Equal(
                "spec.nodeName",
                environment[
                    "AiKubernetesRuntimePoolInPod__KubernetesNodeName"]
                    .ValueFrom
                    .FieldRef
                    .FieldPath);
        }

        /// <summary>
        /// Verifies that the stable Service selects only the exact Pod incarnation plan.
        /// </summary>
        [Fact]
        public void CreateService_Should_Select_Exact_Pod_And_StablePortOnly()
        {
            var spec = CreateSpec("http");
            var factory =
                new AiKubernetesRuntimePoolSdkResourceFactory(
                    CreateHostOptions());

            var service = factory.CreateService(spec);

            Assert.Equal(
                spec.Labels["app.kubernetes.io/instance"],
                service.Spec.Selector["app.kubernetes.io/instance"]);

            var port = Assert.Single(service.Spec.Ports);
            Assert.Equal(spec.Bootstrap.StableTransportPort, port.Port);
            Assert.NotNull(port.TargetPort);
            Assert.Equal("http", port.AppProtocol);
        }

        /// <summary>
        /// Verifies gRPC Service protocol metadata.
        /// </summary>
        [Fact]
        public void CreateService_Should_Advertise_H2c_For_Grpc()
        {
            var spec = CreateSpec("grpc");
            var factory =
                new AiKubernetesRuntimePoolSdkResourceFactory(
                    CreateHostOptions());

            var service = factory.CreateService(spec);

            Assert.Equal(
                "kubernetes.io/h2c",
                Assert.Single(service.Spec.Ports).AppProtocol);
        }

        /// <summary>
        /// Creates one Runtime Pool Pod specification.
        /// </summary>
        private static AiKubernetesRuntimePoolPodSpec CreateSpec(
            string transport)
        {
            var poolOptions = CreatePoolOptions(transport);
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    "request-0001",
                    "primary-runtime-001");

            return new AiKubernetesRuntimePoolPodSpecBuilder(
                poolOptions,
                CreateHostOptions())
                .Build(plan);
        }

        /// <summary>
        /// Creates enabled pool options.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions CreatePoolOptions(
            string transport)
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                Namespace = "runtime-tests",
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix = "runtime-pool",
                ProviderName = transport,
                TransportName = transport,
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 18080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// Creates Kubernetes lifecycle options.
        /// </summary>
        private static AiKubernetesRuntimePoolHostOptions CreateHostOptions()
        {
            return new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage = "multiplexed-ai-runtime:test",
                ContainerName = "runtime-pool",
                CreateService = true,
                ServiceType = "ClusterIP"
            };
        }
    }
}
