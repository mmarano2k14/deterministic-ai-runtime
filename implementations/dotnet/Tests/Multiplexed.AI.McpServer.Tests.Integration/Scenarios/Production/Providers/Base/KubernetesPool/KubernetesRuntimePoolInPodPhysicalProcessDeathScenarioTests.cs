using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Fake;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Isolates the physical in-Pod runtime failure boundary from DAG/recovery semantics.
    /// The Windows test host drives kubectl, while the authoritative process identity is read
    /// from the Linux container's /proc filesystem.
    /// </summary>
    public sealed class KubernetesRuntimePoolInPodPhysicalProcessDeathScenarioTests
    {
        private const string ControlPlaneId = "cp-kubernetes-pool-5d";
        private static readonly TimeSpan FocusedRegistryTtl =
            TimeSpan.FromSeconds(10);

        /// <summary>
        /// Starts one real Runtime Pool Pod with one runtime process, proves the exact Linux
        /// process incarnation dies, proves its heartbeat stops, proves its registry lease expires,
        /// and proves the in-Pod manager creates a fresh replacement identity in the same Pod.
        /// </summary>
        [Fact]
        [Trait("ValidationProfile", "FocusedPhysicalProcessDeath")]
        public async Task KubernetesPool_Should_Prove_Exact_InPod_Runtime_Process_Death_And_Fresh_Replacement_Identity()
        {
            var suffix =
                Guid.NewGuid()
                    .ToString("N")[..8];

            var poolId =
                string.Concat(
                    "pool-physical-death-",
                    suffix);

            var poolOptions =
                CreateSingleRuntimePoolOptions(poolId);

            var hostOptions =
                CreateHostOptions();

            hostOptions.ChildEnvironmentVariables[
                "AiRuntimeInstanceRegistration__RegistryTtl"] =
                FocusedRegistryTtl.ToString(
                    "c",
                    CultureInfo.InvariantCulture);
            hostOptions.ChildEnvironmentVariables[
                "AiRuntimeInstanceRegistration__CapacityTtl"] =
                FocusedRegistryTtl.ToString(
                    "c",
                    CultureInfo.InvariantCulture);

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

            await using var redis =
                await ConnectionMultiplexer.ConnectAsync(
                    "localhost:6379,abortConnect=false");

            var registry =
                new RedisAiRuntimeInstanceRegistry(
                    redis,
                    Options.Create(
                        new AiRuntimeInstanceRegistrationOptions
                        {
                            RegistryTtl = FocusedRegistryTtl,
                            CapacityTtl = FocusedRegistryTtl
                        }),
                    new StaticControlPlaneIdResolver(
                        ControlPlaneId));

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

                var original =
                    await WaitForRuntimeAsync(
                        registry,
                        poolId,
                        runtime =>
                            !runtime.RuntimeInstanceId.Contains(
                                "-replacement-",
                                StringComparison.Ordinal),
                        TimeSpan.FromMinutes(1));

                Assert.NotNull(original.ProcessId);
                Assert.True(original.ProcessId.Value > 0);
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        original.KubernetesPodName));
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        original.KubernetesNamespace));
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        original.HostId));

                await using var killSession =
                    await KubernetesRuntimePoolProductionInfrastructure
                        .PrearmRuntimeProcessKillAsync(
                            original.RuntimeInstanceId,
                            original.KubernetesPodName!,
                            original.KubernetesNamespace!,
                            original.ProcessId.Value,
                            TimeSpan.FromSeconds(15),
                            CancellationToken.None);

                Assert.Equal(
                    original.ProcessId.Value,
                    killSession.ProcessId);
                Assert.True(
                    killSession.ProcessStartTimeTicks > 0);

                var lastHeartbeatBeforeKill =
                    original.LastHeartbeatAtUtc;

                var killResult =
                    await killSession.TriggerKillAsync(
                        TimeSpan.FromSeconds(10),
                        CancellationToken.None);

                Assert.Equal(0, killResult.ExitCode);
                Assert.Equal(
                    original.ProcessId.Value,
                    killResult.ExactProcessDeathProof.ProcessId);
                Assert.Equal(
                    killSession.ProcessStartTimeTicks,
                    killResult.ExactProcessDeathProof.StartTimeTicks);
                Assert.Contains(
                    killResult.ExactProcessDeathProof.Proof,
                    new[]
                    {
                        "PROC_ABSENT",
                        "ZOMBIE",
                        "PID_REUSED"
                    });

                await AssertHeartbeatStoppedAsync(
                    registry,
                    original.RuntimeInstanceId,
                    lastHeartbeatBeforeKill,
                    TimeSpan.FromSeconds(6));

                var replacement =
                    await WaitForRuntimeAsync(
                        registry,
                        poolId,
                        runtime =>
                            !StringComparer.Ordinal.Equals(
                                runtime.RuntimeInstanceId,
                                original.RuntimeInstanceId) &&
                            runtime.RuntimeInstanceId.Contains(
                                "-replacement-",
                                StringComparison.Ordinal),
                        TimeSpan.FromSeconds(30));

                Assert.NotEqual(
                    original.RuntimeInstanceId,
                    replacement.RuntimeInstanceId);
                Assert.Equal(
                    original.HostId,
                    replacement.HostId);
                Assert.Equal(
                    original.KubernetesPodName,
                    replacement.KubernetesPodName);

                await WaitForRuntimeLeaseToExpireAsync(
                    registry,
                    original.RuntimeInstanceId,
                    FocusedRegistryTtl + TimeSpan.FromSeconds(10));

                Console.WriteLine(
                    $"[KUBERNETES_PHYSICAL_PROCESS_DEATH_PROOF] " +
                    $"Status='PASS', PoolId='{poolId}', " +
                    $"OriginalRuntimeInstanceId='{original.RuntimeInstanceId}', " +
                    $"ReplacementRuntimeInstanceId='{replacement.RuntimeInstanceId}', " +
                    $"PodUid='{original.HostId}', " +
                    $"ProcessId='{original.ProcessId}', " +
                    $"LinuxProcessStartTimeTicks='{killSession.ProcessStartTimeTicks}', " +
                    $"ExactProcessDeathProof='{killResult.ExactProcessDeathProof.Proof}', " +
                    "HeartbeatStopped='True', OldRegistryLeaseExpired='True', FreshReplacementIdentity='True'.");
            }
            finally
            {
                await client.DeleteRuntimePoolHostAsync(
                    podSpec);
            }
        }

        private static async Task<AiRuntimeInstanceSnapshot>
            WaitForRuntimeAsync(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                Func<AiRuntimeInstanceSnapshot, bool> predicate,
                TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var runtime =
                    (await registry.ListAsync(
                            includeStopped: false)
                        .ConfigureAwait(false))
                        .Where(item =>
                            StringComparer.Ordinal.Equals(
                                item.PoolId,
                                poolId))
                        .FirstOrDefault(predicate);

                if (runtime is not null)
                {
                    return runtime;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"The focused Kubernetes physical-process proof did not observe the expected runtime. PoolId='{poolId}', Timeout='{timeout}'.");
        }

        private static async Task AssertHeartbeatStoppedAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            DateTimeOffset lastHeartbeatBeforeKill,
            TimeSpan observationWindow)
        {
            var first =
                await registry.GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            await Task.Delay(observationWindow)
                .ConfigureAwait(false);

            var second =
                await registry.GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            if (second is null)
            {
                return;
            }

            var firstHeartbeat =
                first?.LastHeartbeatAtUtc ?? lastHeartbeatBeforeKill;

            Assert.Equal(
                firstHeartbeat,
                second.LastHeartbeatAtUtc);
        }

        private static async Task WaitForRuntimeLeaseToExpireAsync(
            IAiRuntimeInstanceRegistry registry,
            string runtimeInstanceId,
            TimeSpan timeout)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await registry.GetAsync(runtimeInstanceId)
                        .ConfigureAwait(false) is null)
                {
                    return;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            var last =
                await registry.GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            throw new TimeoutException(
                $"The killed runtime registry lease did not expire. RuntimeInstanceId='{runtimeInstanceId}', LastHeartbeatAtUtc='{last?.LastHeartbeatAtUtc:O}', Timeout='{timeout}'.");
        }

        private static AiKubernetesRuntimePoolOptions
            CreateSingleRuntimePoolOptions(
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
                InitialRuntimeInstanceCount = 1,
                MinimumRuntimeInstanceCount = 1,
                MaximumRuntimeInstanceCount = 1,
                StartupParallelism = 1,
                StableTransportPort = 8080,
                FirstChildTransportPort = 18080,
                ChildTransportPortStride = 1,
                ShutdownTimeoutSeconds = 30
            };
        }

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
                    "kubernetes-pool-physical-death-not-used"
            };
        }

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
                ControlPlaneId = ControlPlaneId,
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
                WorkerCountPerInstance = 1,
                MaxConcurrentRunsPerInstance = 1,
                LocalQueueCapacity = 10,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-",
                                poolId),
                        Project = "runtime-pool-physical-death",
                        UserId = "runtime-pool-physical-death",
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
