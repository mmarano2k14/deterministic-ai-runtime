using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Kubernetes;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http.RuntimePool;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Http.KubernetesPool
{
    /// <summary>
    /// Proves exact HTTP command routing through one real Kubernetes Runtime Pool Pod.
    /// </summary>
    [Trait("Category", "HttpKubernetesPool")]
    public sealed class HttpKubernetesPoolMcpCommandScenarioTests
    {
        /// <summary>
        /// Creates one real Pod containing three RuntimeInstanceOnly children, then sends one
        /// exact command to each child through the same stable Service endpoint.
        /// </summary>
        [Fact]
        public async Task Http_KubernetesPool_Should_Route_Exact_Commands_To_All_InPod_Children()
        {
            var suffix =
                Guid.NewGuid()
                    .ToString("N")[..8];

            var poolId =
                string.Concat(
                    "pool-5e-http-",
                    suffix);

            var controlPlaneId =
                string.Concat(
                    "cp-kubernetes-pool-http-5e-",
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

                using var httpClient =
                    new HttpClient
                    {
                        BaseAddress = portForward.Endpoint
                    };

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
                                    HttpRuntimePoolCommandClient
                                        .GetQueueStatusAsync(
                                            httpClient,
                                            runtimeInstanceId,
                                            "http-kubernetes-pool-5e",
                                            timeout.Token)))
                        .ConfigureAwait(false);

                Assert.All(
                    results,
                    result =>
                        Assert.True(
                            result.Success,
                            string.Concat(
                                "HTTP Kubernetes Pool command failed. FailureReason=",
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
        /// Creates a fixed three-child HTTP Runtime Pool topology.
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
                    "kubernetes-pool-http-5e-not-used"
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
                ProviderName = "http",
                TransportName = "http",
                TenantId =
                    "http-kubernetes-pool-5e-tenant",
                TenantGroupId =
                    "http-kubernetes-pool-5e-group",
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
                            "http-kubernetes-pool-5e",
                        UserId =
                            "http-kubernetes-pool-5e",
                        TenantId =
                            "http-kubernetes-pool-5e-tenant",
                        TenantGroupId =
                            "http-kubernetes-pool-5e-group",
                        CurrentNamespace = "tests",
                        Namespaces =
                            new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    }
            };
        }
    }
}
