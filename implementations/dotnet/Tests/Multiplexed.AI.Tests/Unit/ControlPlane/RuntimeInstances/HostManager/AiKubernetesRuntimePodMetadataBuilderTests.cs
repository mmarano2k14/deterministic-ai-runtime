using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Provides unit tests for <see cref="AiKubernetesRuntimePodMetadataBuilder"/>.
    /// </summary>
    public sealed class AiKubernetesRuntimePodMetadataBuilderTests
    {
        /// <summary>
        /// Verifies that Kubernetes metadata keeps Kubernetes as host provider while preserving gRPC as transport provider.
        /// </summary>
        [Fact]
        public void Build_Should_Create_Kubernetes_Metadata_Without_Changing_Runtime_Transport_Provider()
        {
            var builder =
                new AiKubernetesRuntimePodMetadataBuilder(
                    new AiKubernetesRuntimeHostOptions
                    {
                        Namespace = "ai-runtime",
                        PodNamePrefix = "runtime",
                        ContainerName = "runtime-instance",
                        TransportName = "grpc"
                    });

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var metadata = builder.Build(request);

            Assert.Equal("ai-runtime", metadata.Namespace);
            Assert.StartsWith("runtime-runtime-001-", metadata.PodName);
            Assert.Equal("grpc", metadata.Labels["multiplexed.ai/provider"]);
            Assert.Equal("grpc", metadata.Labels["multiplexed.ai/transport"]);
            Assert.Equal("kubernetes", metadata.Labels["multiplexed.ai/host-provider"]);
            Assert.Equal(AiRuntimeHostProviderNames.Kubernetes, metadata.Annotations[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes.ToString(), metadata.Annotations[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.Equal(nameof(KubernetesAiRuntimeHostCreationStrategy), metadata.Annotations[AiRuntimeHostMetadataKeys.HostCreationStrategy]);
            Assert.Equal("ai-runtime", metadata.Annotations[AiKubernetesRuntimeHostMetadataKeys.Namespace]);
            Assert.Equal(metadata.PodName, metadata.Annotations[AiKubernetesRuntimeHostMetadataKeys.PodName]);
            Assert.Equal("runtime-instance", metadata.Annotations[AiKubernetesRuntimeHostMetadataKeys.ContainerName]);
            Assert.Equal("grpc", metadata.Annotations[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal("grpc", metadata.Annotations["transport.name"]);
            Assert.NotEqual("kubernetes", metadata.Annotations[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
        }

        /// <summary>
        /// Verifies that Kubernetes label values are sanitized without mutating annotation values used for diagnostics.
        /// </summary>
        [Fact]
        public void Build_Should_Sanitize_Label_Values()
        {
            var builder =
                new AiKubernetesRuntimePodMetadataBuilder(
                    new AiKubernetesRuntimeHostOptions
                    {
                        Namespace = "ai-runtime",
                        PodNamePrefix = "Runtime_HOST",
                        Labels =
                        {
                            ["custom.label/value"] = "Tenant A Runtime#001"
                        }
                    });

            var request =
                CreateRequest(
                    runtimeInstanceId: "Tenant_A_Runtime#001",
                    providerName: "grpc",
                    transportName: "grpc");

            var metadata = builder.Build(request);

            Assert.StartsWith("runtime-host-runtime-001-", metadata.PodName);
            Assert.Equal("tenant-a-runtime-001", metadata.Labels["multiplexed.ai/runtime-instance-id"]);
            Assert.Equal("tenant-a-runtime-001", metadata.Labels["custom.label/value"]);
        }

        /// <summary>
        /// Verifies that tenant context is preserved in Kubernetes metadata.
        /// </summary>
        [Fact]
        public void Build_Should_Preserve_Tenant_Context_In_Annotations()
        {
            var builder =
                new AiKubernetesRuntimePodMetadataBuilder(
                    new AiKubernetesRuntimeHostOptions
                    {
                        Namespace = "ai-runtime"
                    });

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var metadata = builder.Build(request);

            Assert.Equal("tenant-a", metadata.Labels["multiplexed.ai/tenant-id"]);
            Assert.Equal("tenant-group-a", metadata.Labels["multiplexed.ai/tenant-group-id"]);
            Assert.Equal("tenant-a", metadata.Annotations["tenant.id"]);
            Assert.Equal("tenant-group-a", metadata.Annotations["tenant.groupId"]);
        }

        /// <summary>
        /// Verifies that custom annotations are applied to generated Kubernetes metadata.
        /// </summary>
        [Fact]
        public void Build_Should_Apply_Custom_Annotations()
        {
            var builder =
                new AiKubernetesRuntimePodMetadataBuilder(
                    new AiKubernetesRuntimeHostOptions
                    {
                        Namespace = "ai-runtime",
                        Annotations =
                        {
                            ["custom.annotation/value"] = "raw diagnostic value"
                        }
                    });

            var request =
                CreateRequest(
                    runtimeInstanceId: "tenant-a-runtime-001",
                    providerName: "grpc",
                    transportName: "grpc");

            var metadata = builder.Build(request);

            Assert.Equal("raw diagnostic value", metadata.Annotations["custom.annotation/value"]);
        }

        /// <summary>
        /// Creates a runtime host start request for Kubernetes metadata tests.
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
