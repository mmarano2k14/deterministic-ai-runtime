using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes
{
    /// <summary>
    /// Validates the opt-in Kubernetes Runtime Pool host strategy boundary.
    /// </summary>
    public sealed class KubernetesAiRuntimePoolHostCreationStrategyTests
    {
        /// <summary>
        /// Verifies exact primary runtime preservation and Pod UID host identity.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Create_Exact_PrimaryRuntime_And_PodUidHost()
        {
            var poolOptions = CreatePoolOptions();
            var hostOptions = CreateHostOptions();
            var client =
                new FakeAiKubernetesRuntimePoolHostClient();

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client);

            var request = CreateRequest();

            var result =
                await strategy.StartAsync(request);

            Assert.True(result.Success);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                strategy.Mode);
            Assert.Equal(
                request.RuntimeInstanceId,
                result.RuntimeInstanceId);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(
                request.RuntimeInstanceId,
                client.LastCreatedPodSpec?
                    .Bootstrap
                    .RuntimeInstances[0]
                    .RuntimeInstanceId);
            Assert.StartsWith(
                "fake-pod-uid-",
                result.Metadata[AiRuntimeHostMetadataKeys.HostId]);
            Assert.Equal(
                AiRuntimeHostCreationMode
                    .KubernetesPool
                    .ToString(),
                result.Metadata[
                    AiRuntimeHostMetadataKeys.HostCreationMode]);
        }

        /// <summary>
        /// Verifies that a different first-class PoolId is rejected before Kubernetes calls.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Reject_PoolIdMismatch_Before_Create()
        {
            var poolOptions = CreatePoolOptions();
            var hostOptions = CreateHostOptions();
            var client =
                new FakeAiKubernetesRuntimePoolHostClient();

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client);

            var request =
                CreateRequest() with
                {
                    PoolId = "pool-foreign"
                };

            var result =
                await strategy.StartAsync(request);

            Assert.False(result.Success);
            Assert.Equal(
                "kubernetes-runtime-pool-id-mismatch",
                result.FailureReason);
            Assert.Equal(0, client.CreateCallCount);
        }

        /// <summary>
        /// Verifies failed readiness triggers ownership-safe cleanup.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Delete_CreatedResources_When_ReadinessFails()
        {
            var poolOptions = CreatePoolOptions();
            var hostOptions = CreateHostOptions();
            var client =
                new FakeAiKubernetesRuntimePoolHostClient
                {
                    FailReadiness = true
                };

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client);

            var result =
                await strategy.StartAsync(
                    CreateRequest());

            Assert.False(result.Success);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(1, client.DeleteCallCount);
        }

        /// <summary>
        /// Creates the strategy under test.
        /// </summary>
        private static KubernetesAiRuntimePoolHostCreationStrategy
            CreateStrategy(
                AiKubernetesRuntimePoolOptions poolOptions,
                AiKubernetesRuntimePoolHostOptions hostOptions,
                FakeAiKubernetesRuntimePoolHostClient client)
        {
            return new KubernetesAiRuntimePoolHostCreationStrategy(
                Options.Create(poolOptions),
                Options.Create(hostOptions),
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    poolOptions,
                    hostOptions),
                client,
                new AiKubernetesRuntimePoolInPodCommandLineFactory(
                    hostOptions),
                NullLogger<
                    KubernetesAiRuntimePoolHostCreationStrategy>
                    .Instance);
        }

        /// <summary>
        /// Creates enabled topology options.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions CreatePoolOptions()
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = "pool-shared-01",
                Namespace = "runtime-tests",
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix = "runtime-pool",
                ProviderName = "http",
                TransportName = "http",
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
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
                ServiceType = "ClusterIP",
                DeleteResourcesOnFailure = true
            };
        }

        /// <summary>
        /// Creates one provider scale-out host request.
        /// </summary>
        private static AiRuntimeHostStartRequest CreateRequest()
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId = "scale-out-request-001",
                ControlPlaneId = "control-plane-a",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"),
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = "pool-shared-01",
                RuntimeInstanceId =
                    "tenant-a-runtime-primary-001",
                RuntimeInstanceIdPrefix =
                    "tenant-a-runtime",
                ProviderName = "http",
                TransportName = "http",
                TenantId = "tenant-a",
                TenantGroupId = "tenant-group-a"
            };
        }
    }
}
