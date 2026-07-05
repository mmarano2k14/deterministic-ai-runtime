using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Tests.Fixtures;
using System;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="AiKubernetesRuntimePodSpecBuilder"/>.
    /// </summary>
    public sealed class AiKubernetesRuntimePodSpecBuilderTests
    {
        /// <summary>
        /// Verifies that the pod spec builder creates a runtime-owned Kubernetes pod description.
        /// </summary>
        [Fact]
        public void Build_Should_Create_Runtime_Pod_Spec_From_Host_Start_Request()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ImagePullPolicy = AiKubernetesImagePullPolicy.IfNotPresent,
                    ContainerName = "runtime-instance",
                    ContainerPort = 8081,
                    ServiceAccountName = "runtime-service-account",
                    PodNamePrefix = "runtime"
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal("ai-runtime", spec.Namespace);
            Assert.StartsWith("runtime-tenant-a-runtime-001", spec.PodName);
            Assert.Equal("multiplexed-ai-runtime:test", spec.RuntimeImage);
            Assert.Equal(AiKubernetesImagePullPolicy.IfNotPresent, spec.ImagePullPolicy);
            Assert.Equal("runtime-instance", spec.ContainerName);
            Assert.Equal(8081, spec.ContainerPort);
            Assert.Equal("runtime-service-account", spec.ServiceAccountName);
            Assert.Equal("grpc", spec.Labels["multiplexed.ai/provider"]);
            Assert.Equal("grpc", spec.Labels["multiplexed.ai/transport"]);
            Assert.Equal("kubernetes", spec.Labels["multiplexed.ai/host-provider"]);
            Assert.Equal("kubernetes", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__host.provider"]);
            Assert.Equal("Kubernetes", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__host.creation.mode"]);
        }

        /// <summary>
        /// Verifies that the pod spec preserves gRPC as the runtime transport provider.
        /// </summary>
        [Fact]
        public void Build_Should_Preserve_Runtime_Transport_Provider()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    TransportName = "grpc"
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal("grpc", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__ProviderName"]);
            Assert.Equal("grpc", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__TransportName"]);
            Assert.Equal("grpc", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__provider.name"]);
            Assert.Equal("grpc", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__transport.name"]);
            Assert.NotEqual("kubernetes", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__ProviderName"]);
        }

        /// <summary>
        /// Verifies that tenant context is propagated into runtime registration environment variables.
        /// </summary>
        [Fact]
        public void Build_Should_Propagate_Tenant_Context_To_Runtime_Registration()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test"
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal("tenant-a", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__TenantId"]);
            Assert.Equal("tenant-group-a", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__TenantGroupId"]);
            Assert.Equal("tenant-a", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.id"]);
            Assert.Equal("tenant-group-a", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__tenant.groupId"]);
            Assert.Equal("tenant-a", spec.Labels["multiplexed.ai/tenant-id"]);
            Assert.Equal("tenant-group-a", spec.Labels["multiplexed.ai/tenant-group-id"]);
        }

        /// <summary>
        /// Verifies that the pod spec injects runtime host identity and control-plane identity.
        /// </summary>
        [Fact]
        public void Build_Should_Inject_Runtime_And_ControlPlane_Identity()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test"
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal("RuntimeInstanceOnly", spec.EnvironmentVariables["AiMcpHost__Mode"]);
            Assert.Equal("control-plane-a", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__ControlPlaneId"]);
            Assert.Equal("tenant-a-runtime-001", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__RuntimeInstanceId"]);
            Assert.Equal("http://127.0.0.1:5001", spec.EnvironmentVariables["AiRuntimeInstanceRegistration__Metadata__transport.endpoint"]);
        }

        /// <summary>
        /// Verifies that the pod spec carries generated Kubernetes annotations.
        /// </summary>
        [Fact]
        public void Build_Should_Carry_Kubernetes_Annotations()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ContainerName = "runtime-instance"
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal("kubernetes", spec.Annotations["host.provider"]);
            Assert.Equal("Kubernetes", spec.Annotations["host.creation.mode"]);
            Assert.Equal("ai-runtime", spec.Annotations["kubernetes.namespace"]);
            Assert.Equal(spec.PodName, spec.Annotations["kubernetes.pod.name"]);
            Assert.Equal("runtime-instance", spec.Annotations["kubernetes.container.name"]);
            Assert.Equal("grpc", spec.Annotations["provider.name"]);
            Assert.Equal("grpc", spec.Annotations["transport.name"]);
        }

        /// <summary>
        /// Verifies that the pod spec carries the configured Kubernetes image pull policy.
        /// </summary>
        [Fact]
        public void Build_Should_Carry_Configured_Image_Pull_Policy()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ImagePullPolicy = AiKubernetesImagePullPolicy.Never
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var spec = builder.Build(request);

            Assert.Equal(AiKubernetesImagePullPolicy.Never, spec.ImagePullPolicy);
        }

        /// <summary>
        /// Verifies that build rejects an empty Kubernetes namespace.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Empty_Namespace()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ContainerName = "runtime-instance",
                    ContainerPort = 8080
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            Assert.Throws<ArgumentException>(() => builder.Build(request));
        }

        /// <summary>
        /// Verifies that build rejects an empty runtime image.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Empty_Runtime_Image()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "",
                    ContainerName = "runtime-instance",
                    ContainerPort = 8080
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            Assert.Throws<ArgumentException>(() => builder.Build(request));
        }

        /// <summary>
        /// Verifies that build rejects an empty container name.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Empty_Container_Name()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ContainerName = "",
                    ContainerPort = 8080
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            Assert.Throws<ArgumentException>(() => builder.Build(request));
        }

        /// <summary>
        /// Verifies that build rejects an invalid container port.
        /// </summary>
        [Fact]
        public void Build_Should_Reject_Invalid_Container_Port()
        {
            var options =
                new AiKubernetesRuntimeHostOptions
                {
                    Namespace = "ai-runtime",
                    RuntimeImage = "multiplexed-ai-runtime:test",
                    ContainerName = "runtime-instance",
                    ContainerPort = 0
                };

            var builder =
                new AiKubernetesRuntimePodSpecBuilder(
                    options,
                    new AiKubernetesRuntimePodMetadataBuilder(options));

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var exception =
                Assert.Throws<InvalidOperationException>(
                    () => builder.Build(request));

            Assert.Contains("container port must be greater than zero", exception.Message);
        }

        /// <summary>
        /// Creates a runtime host start request for Kubernetes pod spec tests.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="providerName">The runtime transport provider name.</param>
        /// <param name="transportName">The runtime transport name.</param>
        /// <returns>The runtime host start request.</returns>
        private static AiRuntimeHostStartRequest CreateRequest(
            string runtimeInstanceId,
            string providerName,
            string transportName)
        {
            return new AiRuntimeHostStartRequest
            {
                ControlPlaneId = "control-plane-a",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"),
                RuntimeInstanceId = runtimeInstanceId,
                ProviderName = providerName,
                TransportName = transportName,
                TransportEndpoint = "http://127.0.0.1:5001",
                HostCreationMode = AiRuntimeHostCreationMode.Kubernetes
            };
        }
    }
}