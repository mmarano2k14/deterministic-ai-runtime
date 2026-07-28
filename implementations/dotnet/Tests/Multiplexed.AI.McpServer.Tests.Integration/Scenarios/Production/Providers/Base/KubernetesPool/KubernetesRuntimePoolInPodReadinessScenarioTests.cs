using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Proves that one real Pod becomes Ready only after its in-Pod Process Pool starts.
    /// </summary>
    public sealed class KubernetesRuntimePoolInPodReadinessScenarioTests
    {
        /// <summary>
        /// Creates one real Runtime Pool Pod and waits for exact child-capacity readiness.
        /// </summary>
        [Fact]
        public async Task KubernetesPool_Should_Start_Real_InPod_ProcessPool_And_Reach_Readiness()
        {
            var suffix =
                Guid.NewGuid()
                    .ToString("N")[..8];

            var poolId =
                string.Concat(
                    "pool-5d-",
                    suffix);

            var poolOptions =
                CreatePoolOptions(poolId);

            var hostOptions =
                CreateHostOptions();

            var request =
                CreateRequest(
                    poolId,
                    suffix);

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    string.Concat(
                        "request-",
                        suffix),
                    request.RuntimeInstanceId);

            var baseSpec =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    poolOptions,
                    hostOptions)
                    .Build(plan);

            var podSpec =
                baseSpec with
                {
                    ContainerArguments =
                        new AiKubernetesRuntimePoolInPodCommandLineFactory(
                            hostOptions)
                            .Create(
                                baseSpec,
                                request)
                };

            var resourceFactory =
                new AiKubernetesRuntimePoolSdkResourceFactory(
                    hostOptions);

            var client =
                new KubernetesSdkAiKubernetesRuntimePoolHostClient(
                    new DefaultKubernetesClientFactory(),
                    resourceFactory,
                    hostOptions);

            try
            {
                var createResult =
                    await client.CreateRuntimePoolHostAsync(
                        podSpec);

                Assert.True(
                    createResult.Success,
                    createResult.FailureReason);

                var readinessResult =
                    await client.WaitUntilHostReadyAsync(
                        podSpec);

                Assert.True(
                    readinessResult.Success,
                    readinessResult.FailureReason);
                Assert.Equal(
                    poolId,
                    readinessResult.Metadata[
                        "runtime.pool.id"]);
                Assert.Equal(
                    "3",
                    readinessResult.Metadata[
                        "runtime.pool.plannedRuntimeCount"]);
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        readinessResult.Metadata[
                            AiRuntimeHostMetadataKeys.HostId]));
            }
            finally
            {
                await client.DeleteRuntimePoolHostAsync(
                    podSpec);
            }
        }

        /// <summary>
        /// Creates fixed-size pool topology.
        /// </summary>
        private static AiKubernetesRuntimePoolOptions
            CreatePoolOptions(
                string poolId)
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = poolId,
                Namespace =
                    KubernetesRuntimePoolScenarioConstants
                        .Namespace,
                PodNamePrefix = "runtime-pool",
                RuntimeInstanceIdPrefix =
                    "runtime-pool",
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
        /// Creates Kubernetes lifecycle and in-Pod options.
        /// </summary>
        private static AiKubernetesRuntimePoolHostOptions
            CreateHostOptions()
        {
            return new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage =
                    KubernetesRuntimePoolScenarioConstants
                        .RuntimeImage,
                ContainerName = "runtime-pool",
                ImagePullPolicy =
                    AiKubernetesImagePullPolicy.Never,
                ClientMode =
                    AiKubernetesRuntimeHostClientMode
                        .KubernetesSdk,
                CreateService = true,
                ServiceType = "NodePort",
                NodePortHost =
                    KubernetesRuntimePoolScenarioConstants
                        .NodePortHost,
                StartupTimeout =
                    TimeSpan.FromMinutes(2),
                ReadinessPollInterval =
                    TimeSpan.FromSeconds(1),
                RedisConnectionString =
                    KubernetesRuntimePoolScenarioConstants
                        .RedisConnectionString,
                MongoConnectionString =
                    KubernetesRuntimePoolScenarioConstants
                        .MongoConnectionString,
                MongoDatabaseName =
                    "multiplexed_ai_tests",
                OpenAiApiKey =
                    "kubernetes-pool-5d-not-used"
            };
        }

        /// <summary>
        /// Creates the exact provider host request carried into the Pod bootstrap.
        /// </summary>
        private static AiRuntimeHostStartRequest CreateRequest(
            string poolId,
            string suffix)
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        "scale-request-",
                        suffix),
                ControlPlaneId =
                    "cp-kubernetes-pool-5d",
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId =
                    string.Concat(
                        poolId,
                        "-primary"),
                RuntimeInstanceIdPrefix =
                    string.Concat(
                        poolId,
                        "-runtime"),
                ProviderName = "http",
                TransportName = "http",
                TenantId = "test-tenant",
                TenantGroupId =
                    "test-tenant-group",
                IsolationMode = "Shared",
                AllowSharedFallback = true,
                WorkerCountPerInstance = 3,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 100,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-",
                                poolId),
                        Project = "runtime-pool-5d",
                        UserId = "runtime-pool-5d",
                        TenantId = "test-tenant",
                        TenantGroupId =
                            "test-tenant-group",
                        CurrentNamespace = "tests",
                        Namespaces =
                            new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    }
            };
        }
    }
}
