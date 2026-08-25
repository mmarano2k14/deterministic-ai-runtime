using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Client.Factory;
using Multiplexed.AI.Tests.Fixtures;
using StackExchange.Redis;
using Xunit;

namespace Multiplexed.AI.Tests.Integration.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class KubernetesRuntimePoolPodFailureRecoveryCollection
    {
        public const string Name =
            "Kubernetes Runtime Pool Pod failure recovery collection";
    }

    [Collection(KubernetesRuntimePoolPodFailureRecoveryCollection.Name)]
    [Trait("Category", "KubernetesRuntimePoolPodFailureRecoveryEndToEnd")]
    public sealed class KubernetesRuntimePoolPodFailureRecoveryEndToEndProofTests
    {
        private const string KubernetesNamespace = "ai-runtime";
        private const string MinikubeRedisConnectionString =
            "host.minikube.internal:6379,abortConnect=false";
        private const string MinikubeMongoConnectionString =
            "mongodb://host.minikube.internal:27017/?directConnection=true";

        [Fact]
        public async Task Forced_Pod_Deletion_Should_Suppress_Exact_Membership_Recover_Work_And_Create_Fresh_Pod()
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var poolId = string.Concat("pool-6f-", suffix);
            var controlPlaneId = string.Concat("cp-pool-6f-", suffix);
            var poolOptions = CreatePoolOptions(poolId);
            var hostOptions = CreateHostOptions();
            var failedRequest =
                CreateRequest(
                    poolId,
                    controlPlaneId,
                    suffix,
                    "failed");
            var safeRequest =
                CreateRequest(
                    poolId,
                    controlPlaneId,
                    suffix,
                    "safe");
            var failedPodSpec =
                CreatePodSpec(
                    poolOptions,
                    hostOptions,
                    failedRequest,
                    string.Concat("failed-request-", suffix));
            var safePodSpec =
                CreatePodSpec(
                    poolOptions,
                    hostOptions,
                    safeRequest,
                    string.Concat("safe-request-", suffix));

            var redisConnection =
                await CreateRedisConnectionAsync().ConfigureAwait(false);
            var recoveryFixture =
                new RuntimePoolClaimedRecoveryEndToEndTestFixture();
            var builder = Host.CreateApplicationBuilder();

            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AiEngine:ControlPlane:ControlPlaneId"] =
                        controlPlaneId,
                    ["AiEngine:ControlPlane:RedisDiscoveryKey"] =
                        string.Concat(
                            "multiplexed-ai:",
                            controlPlaneId),
                    ["AiRuntimeInstanceRegistration:Enabled"] = "false"
                });

            builder.Services.AddSingleton<IConnectionMultiplexer>(
                redisConnection);
            builder.Services.AddSingleton<IAiControlPlaneIdResolver>(
                new FixedControlPlaneIdResolver(controlPlaneId));
            builder.Services.AddAiControlPlane(
                configuration: builder.Configuration);
            recoveryFixture.RegisterServices(builder.Services);
            builder.Services.AddAiKubernetesRuntimePoolHostProvider(
                configurePool: options => CopyPoolOptions(poolOptions, options),
                configureHost: options => CopyHostOptions(hostOptions, options));

            using var host = builder.Build();
            using var timeout =
                new CancellationTokenSource(TimeSpan.FromMinutes(5));

            var client =
                new KubernetesSdkAiKubernetesRuntimePoolHostClient(
                    new DefaultKubernetesClientFactory(),
                    new AiKubernetesRuntimePoolSdkResourceFactory(
                        hostOptions),
                    hostOptions);

            AiKubernetesRuntimePoolPodSpec? replacementCleanupSpec = null;

            await host.StartAsync(timeout.Token);

            try
            {
                var failedCreate =
                    await client.CreateRuntimePoolHostAsync(
                        failedPodSpec,
                        timeout.Token);
                Assert.True(failedCreate.Success, failedCreate.FailureReason);

                var failedReady =
                    await client.WaitUntilHostReadyAsync(
                        failedPodSpec,
                        timeout.Token);
                Assert.True(failedReady.Success, failedReady.FailureReason);

                var safeCreate =
                    await client.CreateRuntimePoolHostAsync(
                        safePodSpec,
                        timeout.Token);
                Assert.True(safeCreate.Success, safeCreate.FailureReason);

                var safeReady =
                    await client.WaitUntilHostReadyAsync(
                        safePodSpec,
                        timeout.Token);
                Assert.True(safeReady.Success, safeReady.FailureReason);

                var failedPodUid = GetHostId(failedReady.Metadata);
                var safePodUid = GetHostId(safeReady.Metadata);
                Assert.NotEqual(failedPodUid, safePodUid);

                var membershipEnumerator =
                    host.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodMembershipEnumerator>();
                var failedMembership =
                    await WaitForReadyMembershipAsync(
                        membershipEnumerator,
                        poolId,
                        failedPodUid,
                        timeout.Token);
                var safeMembership =
                    await WaitForReadyMembershipAsync(
                        membershipEnumerator,
                        poolId,
                        safePodUid,
                        timeout.Token);

                Assert.Equal(3, failedMembership.Members.Count);
                Assert.Equal(3, safeMembership.Members.Count);

                var state =
                    host.Services.GetRequiredService<
                        RuntimePoolClaimedRecoveryEndToEndState>();
                var failedRuntimeIds =
                    failedMembership.Members
                        .Select(member => member.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                recoveryFixture.SeedAssignedWork(
                    state,
                    failedRuntimeIds[0],
                    failedRuntimeIds[1],
                    failedRuntimeIds[2]);

                var forcedDelete =
                    await RunKubectlAsync(
                        timeout.Token,
                        "delete",
                        "pod",
                        failedPodSpec.PodName,
                        "--namespace",
                        failedPodSpec.Namespace,
                        "--grace-period=0",
                        "--force",
                        "--wait=true",
                        "--timeout=90s");

                Assert.Equal(0, forcedDelete.ExitCode);

                var oldServiceName =
                    new AiKubernetesRuntimePoolSdkResourceFactory(
                        hostOptions)
                        .CreateServiceName(failedPodSpec);

                await WaitForServiceWithoutEndpointsAsync(
                    failedPodSpec.Namespace,
                    oldServiceName,
                    timeout.Token);

                var coordinator =
                    host.Services.GetRequiredService<
                        IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator>();
                var failureId =
                    string.Concat("pod-failure-", suffix);
                var firstRecovery =
                    coordinator.RecoverAsync(
                        new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                        {
                            FailureId = failureId,
                            PoolId = poolId,
                            PodUid = failedPodUid,
                            ClaimedBy =
                                string.Concat("reconciler-a-", suffix),
                            FailureMessage =
                                "forced Kubernetes Pod deletion proof",
                            HostStartTemplate = failedRequest
                        },
                        timeout.Token);

                var competingRecovery =
                    coordinator.RecoverAsync(
                        new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                        {
                            FailureId = failureId,
                            PoolId = poolId,
                            PodUid = failedPodUid,
                            ClaimedBy =
                                string.Concat("reconciler-b-", suffix),
                            FailureMessage =
                                "concurrent forced Pod deletion proof",
                            HostStartTemplate = failedRequest
                        },
                        timeout.Token);

                var recoveryResults =
                    await Task.WhenAll(
                        firstRecovery,
                        competingRecovery);

                var result =
                    Assert.Single(recoveryResults, item =>
                                item.Status ==
                                    AiRuntimePoolRecoveryClaimAcquisitionStatus
                                        .Acquired);

                var duplicate =
                    Assert.Single(recoveryResults, item =>
                                item.Status ==
                                    AiRuntimePoolRecoveryClaimAcquisitionStatus
                                        .AlreadyClaimed);

                Assert.Null(duplicate.Replacement);
                Assert.Null(duplicate.Recovery);
                Assert.Equal(result.Failure, duplicate.Failure);
                Assert.Equal(
                    result.Suppression.FailureId,
                    duplicate.Suppression.FailureId);
                Assert.Equal(
                    result.Suppression.PoolId,
                    duplicate.Suppression.PoolId);
                Assert.Equal(
                    result.Suppression.PodUid,
                    duplicate.Suppression.PodUid);
                Assert.Equal(
                    result.Suppression.SuppressedAtUtc,
                    duplicate.Suppression.SuppressedAtUtc);
                Assert.Equal(
                    result.Suppression.Suppressions.ToArray(),
                    duplicate.Suppression.Suppressions.ToArray());

                Assert.Equal(
                    AiRuntimePoolFailureScope.Host,
                    result.Failure.Scope);
                Assert.Equal(
                    AiRuntimePoolFailureKind.UnexpectedPodDeletion,
                    result.Failure.Kind);
                Assert.Null(result.Failure.RuntimeInstanceId);
                Assert.Null(result.Failure.RouteId);

                Assert.Equal(3, result.Suppression.Suppressions.Count);
                Assert.Equal(
                    failedRuntimeIds,
                    result.Suppression.Suppressions
                        .Select(item => item.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray());
                Assert.All(
                    result.Suppression.Suppressions,
                    item =>
                    {
                        Assert.Equal(
                            AiRuntimePoolCapacitySuppressionScope
                                .HostMembership,
                            item.Scope);
                        Assert.Null(item.RouteId);
                    });

                Assert.NotNull(result.Replacement);
                Assert.NotNull(result.Recovery);
                Assert.NotEqual(
                    failedPodUid,
                    result.Replacement!.ReplacementPodUid);
                Assert.Equal(3, result.Replacement.Membership.Members.Count);
                Assert.All(
                    result.Replacement.Membership.Members,
                    member =>
                    {
                        Assert.Equal(
                            AiRuntimeInstanceStatus.Ready,
                            member.Status);
                        Assert.True(member.CanAcceptRun);
                        Assert.DoesNotContain(
                            member.RuntimeInstanceId,
                            failedRuntimeIds);
                    });

                Assert.Equal(5, result.Recovery!.CandidateCount);
                Assert.Equal(5, result.Recovery.AcceptedCount);
                Assert.Equal(5, result.Recovery.ChangedCount);
                Assert.Equal(0, result.Recovery.RejectedCount);
                Assert.Equal(5, state.TransitionRequests.Count);
                Assert.DoesNotContain(
                    state.TransitionRequests,
                    request =>
                        result.Replacement.Membership.Members.Any(
                            member =>
                                StringComparer.Ordinal.Equals(
                                    member.RuntimeInstanceId,
                                    request.Ownership.RuntimeInstanceId)));

                var safety =
                    host.Services.GetRequiredService<
                        IAiRuntimePoolCapacitySafetyReader>();

                foreach (var safeMember in safeMembership.Members)
                {
                    var safeSuppression =
                        await safety.GetSuppressionAsync(
                            poolId,
                            safePodUid,
                            safeMember.RuntimeInstanceId,
                            timeout.Token);

                    Assert.Null(safeSuppression);
                }

                var safeMembershipAfterRecovery =
                    await WaitForReadyMembershipAsync(
                        membershipEnumerator,
                        poolId,
                        safePodUid,
                        timeout.Token);

                Assert.Equal(
                    safeMembership.Members
                        .Select(member => member.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal),
                    safeMembershipAfterRecovery.Members
                        .Select(member => member.RuntimeInstanceId)
                        .OrderBy(value => value, StringComparer.Ordinal));

                Assert.True(
                    result.Replacement.HostStartResult.Metadata.TryGetValue(
                        AiRuntimeHostMetadataKeys.HostName,
                        out var replacementPodName));
                Assert.False(string.IsNullOrWhiteSpace(replacementPodName));

                replacementCleanupSpec =
                    failedPodSpec with
                    {
                        PodName = replacementPodName!
                    };
            }
            finally
            {
                using var cleanup =
                    new CancellationTokenSource(TimeSpan.FromMinutes(2));

                if (replacementCleanupSpec is not null)
                {
                    await client.DeleteRuntimePoolHostAsync(
                        replacementCleanupSpec,
                        cleanup.Token);
                }

                await client.DeleteRuntimePoolHostAsync(
                    failedPodSpec,
                    cleanup.Token);
                await client.DeleteRuntimePoolHostAsync(
                    safePodSpec,
                    cleanup.Token);

                await host.StopAsync(cleanup.Token);
                await redisConnection.CloseAsync();
                redisConnection.Dispose();
            }
        }

        private static AiKubernetesRuntimePoolOptions CreatePoolOptions(
            string poolId)
        {
            return new AiKubernetesRuntimePoolOptions
            {
                Enabled = true,
                PoolId = poolId,
                Namespace = KubernetesNamespace,
                PodNamePrefix = "runtime-pool-6f",
                RuntimeInstanceIdPrefix = "runtime-pool-6f",
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

        private static AiKubernetesRuntimePoolHostOptions CreateHostOptions()
        {
            return new AiKubernetesRuntimePoolHostOptions
            {
                RuntimeImage = "multiplexed-ai-runtime:k8s-debug-135",
                ContainerName = "runtime-pool",
                ImagePullPolicy = AiKubernetesImagePullPolicy.Never,
                ClientMode =
                    AiKubernetesRuntimeHostClientMode.KubernetesSdk,
                CreateService = true,
                ServiceType = "NodePort",
                NodePortHost = "192.168.49.2",
                StartupTimeout = TimeSpan.FromMinutes(2),
                ReadinessPollInterval = TimeSpan.FromSeconds(1),
                RedisConnectionString = MinikubeRedisConnectionString,
                MongoConnectionString = MinikubeMongoConnectionString,
                MongoDatabaseName = "multiplexed_ai_tests",
                OpenAiApiKey = "kubernetes-pool-6f-not-used"
            };
        }

        private static void CopyPoolOptions(
            AiKubernetesRuntimePoolOptions source,
            AiKubernetesRuntimePoolOptions target)
        {
            target.Enabled = source.Enabled;
            target.PoolId = source.PoolId;
            target.Namespace = source.Namespace;
            target.PodNamePrefix = source.PodNamePrefix;
            target.RuntimeInstanceIdPrefix =
                source.RuntimeInstanceIdPrefix;
            target.ProviderName = source.ProviderName;
            target.TransportName = source.TransportName;
            target.InitialRuntimeInstanceCount =
                source.InitialRuntimeInstanceCount;
            target.MinimumRuntimeInstanceCount =
                source.MinimumRuntimeInstanceCount;
            target.MaximumRuntimeInstanceCount =
                source.MaximumRuntimeInstanceCount;
            target.StartupParallelism = source.StartupParallelism;
            target.StableTransportPort = source.StableTransportPort;
            target.FirstChildTransportPort =
                source.FirstChildTransportPort;
            target.ChildTransportPortStride =
                source.ChildTransportPortStride;
            target.ShutdownTimeoutSeconds =
                source.ShutdownTimeoutSeconds;
        }

        private static void CopyHostOptions(
            AiKubernetesRuntimePoolHostOptions source,
            AiKubernetesRuntimePoolHostOptions target)
        {
            target.RuntimeImage = source.RuntimeImage;
            target.ContainerName = source.ContainerName;
            target.ImagePullPolicy = source.ImagePullPolicy;
            target.ClientMode = source.ClientMode;
            target.CreateService = source.CreateService;
            target.ServiceType = source.ServiceType;
            target.NodePortHost = source.NodePortHost;
            target.StartupTimeout = source.StartupTimeout;
            target.ReadinessPollInterval =
                source.ReadinessPollInterval;
            target.RedisConnectionString = source.RedisConnectionString;
            target.MongoConnectionString = source.MongoConnectionString;
            target.MongoDatabaseName = source.MongoDatabaseName;
            target.OpenAiApiKey = source.OpenAiApiKey;
        }

        private static AiRuntimeHostStartRequest CreateRequest(
            string poolId,
            string controlPlaneId,
            string suffix,
            string role)
        {
            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat("host-", role, "-", suffix),
                ControlPlaneId = controlPlaneId,
                HostCreationMode =
                    AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId =
                    string.Concat(
                        poolId,
                        "-",
                        role,
                        "-primary"),
                RuntimeInstanceIdPrefix =
                    string.Concat(poolId, "-", role, "-runtime"),
                ProviderName = "http",
                TransportName = "http",
                TenantId = string.Concat("tenant-", role),
                TenantGroupId = "tenant-group-6f",
                IsolationMode = "Shared",
                AllowSharedFallback = true,
                WorkerCountPerInstance = 3,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 100,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat("ctx-", role, "-", suffix),
                        Project = "kubernetes-pool-6f",
                        UserId = "system",
                        TenantId = string.Concat("tenant-", role),
                        TenantGroupId = "tenant-group-6f",
                        CurrentNamespace = "tests",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    },
                Metadata = new Dictionary<string, string>()
            };
        }

        private static AiKubernetesRuntimePoolPodSpec CreatePodSpec(
            AiKubernetesRuntimePoolOptions poolOptions,
            AiKubernetesRuntimePoolHostOptions hostOptions,
            AiRuntimeHostStartRequest request,
            string podRequestId)
        {
            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    podRequestId,
                    request.RuntimeInstanceId);
            var baseSpec =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    poolOptions,
                    hostOptions)
                    .Build(plan);

            return baseSpec with
            {
                ContainerArguments =
                    new AiKubernetesRuntimePoolInPodCommandLineFactory(
                        hostOptions)
                        .Create(baseSpec, request)
            };
        }

        private static async Task<AiKubernetesRuntimePoolPodMembership>
            WaitForReadyMembershipAsync(
                IAiKubernetesRuntimePoolPodMembershipEnumerator enumerator,
                string poolId,
                string podUid,
                CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var membership =
                        await enumerator.EnumerateAsync(
                            poolId,
                            podUid,
                            cancellationToken);

                    if (membership.Members.Count == 3 &&
                        membership.Members.All(
                            member =>
                                member.Status ==
                                    AiRuntimeInstanceStatus.Ready &&
                                member.CanAcceptRun))
                    {
                        return membership;
                    }
                }
                catch (
                    AiKubernetesRuntimePoolPodMembershipAuthorityException
                    exception)
                    when (exception.Reason ==
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .MembershipNotFound)
                {
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(200),
                    cancellationToken);
            }
        }

        private static string GetHostId(
            IReadOnlyDictionary<string, string> metadata)
        {
            Assert.True(
                metadata.TryGetValue(
                    AiRuntimeHostMetadataKeys.HostId,
                    out var hostId));
            Assert.False(string.IsNullOrWhiteSpace(hostId));
            return hostId!;
        }

        private static async Task WaitForServiceWithoutEndpointsAsync(
            string @namespace,
            string serviceName,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result =
                    await RunKubectlAsync(
                        cancellationToken,
                        "get",
                        "endpoints",
                        serviceName,
                        "--namespace",
                        @namespace,
                        "--output=jsonpath={.subsets}");

                var output = result.StandardOutput.Trim();
                var endpointSetIsEmpty =
                    string.IsNullOrWhiteSpace(output) ||
                    StringComparer.Ordinal.Equals(output, "[]") ||
                    StringComparer.OrdinalIgnoreCase.Equals(
                        output,
                        "<no value>") ||
                    StringComparer.OrdinalIgnoreCase.Equals(
                        output,
                        "null");
                var resourceIsGone =
                    result.ExitCode != 0 &&
                    result.StandardError.Contains(
                        "NotFound",
                        StringComparison.OrdinalIgnoreCase);

                if ((result.ExitCode == 0 && endpointSetIsEmpty) ||
                    resourceIsGone)
                {
                    return;
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(250),
                    cancellationToken);
            }
        }

        private static async Task<KubectlResult> RunKubectlAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process =
                new System.Diagnostics.Process
                {
                    StartInfo = startInfo
                };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "kubectl could not be started.");
            }

            var standardOutputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            return new KubectlResult(
                process.ExitCode,
                await standardOutputTask,
                await standardErrorTask);
        }

        private static async Task<IConnectionMultiplexer>
            CreateRedisConnectionAsync()
        {
            var configuration =
                new ConfigurationOptions
                {
                    AbortOnConnectFail = false,
                    ConnectRetry = 2,
                    ConnectTimeout = 5000,
                    SyncTimeout = 5000
                };

            configuration.EndPoints.Add("127.0.0.1", 6379);

            return await ConnectionMultiplexer.ConnectAsync(configuration);
        }

        private sealed record KubectlResult(
            int ExitCode,
            string StandardOutput,
            string StandardError);

        private sealed class FixedControlPlaneIdResolver :
            IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            public FixedControlPlaneIdResolver(string controlPlaneId)
            {
                this.controlPlaneId = controlPlaneId;
            }

            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.controlPlaneId);
            }

            public Task<string> ResolveAsync(
                AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.controlPlaneId);
            }

            public Task<IReadOnlyDictionary<string, string>>
                ResolveMetadataAsync(
                    AiControlPlaneIdResolutionRequest request,
                    CancellationToken cancellationToken = default)
            {
                IReadOnlyDictionary<string, string> metadata =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["controlPlaneId"] = this.controlPlaneId,
                        ["logicalControlPlaneId"] = this.controlPlaneId,
                        ["runtime.controlPlaneId"] = this.controlPlaneId
                    };

                return Task.FromResult(metadata);
            }
        }
    }
}
