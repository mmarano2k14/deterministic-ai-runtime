using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Grpc.RuntimePool;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http.RuntimePool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Xunit;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Grpc.KubernetesPool
{
    /// <summary>
    /// Proves exact gRPC command routing through one real Kubernetes Runtime Pool Pod.
    /// </summary>
    [Trait("Category", "GrpcKubernetesPool")]
    public sealed class GrpcKubernetesPoolMcpCommandScenarioTests
    {
        /// <summary>
        /// Creates one real Pod containing three RuntimeInstanceOnly children, then sends one
        /// exact command to each child through the same stable HTTP/2 Service endpoint.
        /// </summary>
        [Fact]
        public async Task Grpc_KubernetesPool_Should_Route_Exact_Commands_To_All_InPod_Children()
        {
            var suffix =
                Guid.NewGuid()
                    .ToString("N")[..8];

            var poolId =
                string.Concat(
                    "pool-5f-grpc-",
                    suffix);

            var controlPlaneId =
                string.Concat(
                    "cp-kubernetes-pool-grpc-5f-",
                    suffix);

            var poolOptions =
                CreatePoolOptions(poolId);

            var hostOptions =
                CreateHostOptions();

            var request =
                CreateRequest(
                    poolId,
                    controlPlaneId,
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
                    await client
                        .CreateRuntimePoolHostAsync(podSpec)
                        .ConfigureAwait(false);

                Assert.True(
                    createResult.Success,
                    createResult.FailureReason);

                var readinessResult =
                    await client
                        .WaitUntilHostReadyAsync(podSpec)
                        .ConfigureAwait(false);

                Assert.True(
                    readinessResult.Success,
                    readinessResult.FailureReason);

                Assert.True(
                    readinessResult.Metadata.TryGetValue(
                        AiKubernetesRuntimeHostMetadataKeys.ServiceName,
                        out var serviceName));

                Assert.False(
                    string.IsNullOrWhiteSpace(serviceName));

                using var timeout =
                    new CancellationTokenSource(
                        TimeSpan.FromMinutes(2));

                await using var portForward =
                    await KubernetesServicePortForward
                        .StartAsync(
                            podSpec.Namespace,
                            serviceName!,
                            podSpec.Bootstrap.StableTransportPort,
                            timeout.Token)
                        .ConfigureAwait(false);

                using var channel =
                    GrpcChannel.ForAddress(
                        portForward.Endpoint);

                var grpcClient =
                    new AiRuntimeInstanceCommandGrpc
                        .AiRuntimeInstanceCommandGrpcClient(
                            channel);

                var runtimeInstanceIds =
                    podSpec.Bootstrap.RuntimeInstances
                        .OrderBy(runtime => runtime.Ordinal)
                        .Select(runtime => runtime.RuntimeInstanceId)
                        .ToArray();

                Assert.Equal(
                    3,
                    runtimeInstanceIds.Length);

                var results =
                    await Task.WhenAll(
                            runtimeInstanceIds.Select(
                                runtimeInstanceId =>
                                    GrpcRuntimePoolCommandClient
                                        .GetQueueStatusAsync(
                                            grpcClient,
                                            runtimeInstanceId,
                                            "grpc-kubernetes-pool-5f",
                                            timeout.Token)))
                        .ConfigureAwait(false);

                Assert.All(
                    results,
                    result =>
                        Assert.True(
                            result.Success,
                            string.Concat(
                                "gRPC Kubernetes Pool command failed. FailureReason=",
                                result.FailureReason ?? "<null>",
                                "; Message=",
                                result.Message ?? "<null>",
                                "; RuntimeInstanceId=",
                                result.RuntimeInstanceId)));

                Assert.Equal(
                    runtimeInstanceIds
                        .OrderBy(value => value, StringComparer.Ordinal),
                    results
                        .Select(result => result.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal));

                Assert.Equal(
                    3,
                    results
                        .Select(result => result.RuntimeInstanceId)
                        .Distinct(StringComparer.Ordinal)
                        .Count());
            }
            finally
            {
                await client
                    .DeleteRuntimePoolHostAsync(podSpec)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates a fixed three-child gRPC Runtime Pool topology.
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
                ProviderName = "grpc",
                TransportName = "grpc",
                InitialRuntimeInstanceCount = 3,
                MinimumRuntimeInstanceCount = 3,
                MaximumRuntimeInstanceCount = 3,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                ReadinessPort = 8081,
                FirstChildTransportPort = 19080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

        /// <summary>
        /// Creates the Kubernetes SDK and in-Pod settings.
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
                    "kubernetes-pool-grpc-5f-not-used"
            };
        }

        /// <summary>
        /// Creates the exact KubernetesPool host request.
        /// </summary>
        private static AiRuntimeHostStartRequest CreateRequest(
            string poolId,
            string controlPlaneId,
            string suffix)
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        "scale-request-",
                        suffix),
                ControlPlaneId =
                    controlPlaneId,
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
                ProviderName = "grpc",
                TransportName = "grpc",
                TenantId =
                    "grpc-kubernetes-pool-5f-tenant",
                TenantGroupId =
                    "grpc-kubernetes-pool-5f-group",
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
                        Project =
                            "grpc-kubernetes-pool-5f",
                        UserId =
                            "grpc-kubernetes-pool-5f",
                        TenantId =
                            "grpc-kubernetes-pool-5f-tenant",
                        TenantGroupId =
                            "grpc-kubernetes-pool-5f-group",
                        CurrentNamespace = "tests",
                        Namespaces =
                            new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    }
            };
        }
    }
}
