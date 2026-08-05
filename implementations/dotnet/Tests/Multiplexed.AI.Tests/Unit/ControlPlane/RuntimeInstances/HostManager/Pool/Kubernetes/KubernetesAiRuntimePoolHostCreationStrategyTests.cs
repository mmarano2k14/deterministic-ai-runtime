using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport;
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
            var capacityStore =
                new RuntimePoolCapacityStore(client);

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client,
                    capacityStore);

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

            var descriptors =
                await capacityStore.ListAsync();

            Assert.Equal(3, descriptors.Count);

            foreach (var descriptor in descriptors)
            {
                Assert.Equal(
                    result.TransportEndpoint,
                    descriptor.Metadata["transport.endpoint"]);
                Assert.Equal(
                    result.TransportEndpoint,
                    descriptor.Metadata["grpc.endpoint"]);
                Assert.Equal(
                    "preserved-existing-capacity-descriptor",
                    descriptor.Metadata["transport.endpoint.source"]);
                Assert.Equal(
                    "control-plane",
                    descriptor.Metadata["transport.endpoint.scope"]);
                Assert.StartsWith(
                    "http://127.0.0.1:",
                    descriptor.Metadata["transport.endpoint.internal"]);
                Assert.True(descriptor.CanAcceptRun);
                Assert.Equal(AiRuntimeInstanceStatus.Ready, descriptor.Status);
            }
        }

        /// <summary>
        /// Verifies that Gateway mode publishes one shared host-local endpoint while creating
        /// one exact route per child to the stable Runtime Pool Service.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Project_SharedGatewayEndpoint_And_ExactChildRoutes()
        {
            var poolOptions = CreatePoolOptions();
            poolOptions.ProviderName = "grpc";
            poolOptions.TransportName = "grpc";
            var hostOptions = CreateHostOptions();
            hostOptions.UseGatewayTransportEndpoint = true;

            var client =
                new FakeAiKubernetesRuntimePoolHostClient();
            var capacityStore =
                new RuntimePoolCapacityStore(client);
            var gatewayManager =
                new RecordingGatewayManager();
            using var gatewayTransportEndpointManager =
                new FakeGatewayTransportEndpointManager();

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client,
                    capacityStore,
                    gatewayManager,
                    gatewayTransportEndpointManager);

            var result =
                await strategy.StartAsync(
                    CreateRequest() with
                    {
                        ProviderName = "grpc",
                        TransportName = "grpc"
                    });

            Assert.True(result.Success);
            Assert.Equal(
                FakeGatewayTransportEndpointManager.ExternalEndpoint,
                result.TransportEndpoint);
            Assert.Equal(
                "x-ai-runtime-instance-id",
                result.Metadata["gateway.routing.header"]);
            Assert.Equal(
                result.RuntimeInstanceId,
                result.Metadata["gateway.routing.value"]);
            Assert.Equal(3, gatewayManager.GrpcRoutes.Count);
            Assert.Empty(gatewayManager.HttpRoutes);

            var backendServiceName =
                Assert.Single(
                    gatewayManager.GrpcRoutes
                        .Select(route => route.RuntimeServiceName)
                        .Distinct(StringComparer.Ordinal));

            Assert.False(string.IsNullOrWhiteSpace(backendServiceName));
            Assert.All(
                gatewayManager.GrpcRoutes,
                route =>
                {
                    Assert.Equal(8080, route.BackendPort);
                    Assert.Equal(
                        route.RuntimeInstanceId,
                        route.RoutingHeaderValue);
                    Assert.Equal(
                        "x-ai-runtime-instance-id",
                        route.RoutingHeaderName);
                });

            var descriptors =
                await capacityStore.ListAsync();

            Assert.Equal(3, descriptors.Count);
            Assert.All(
                descriptors,
                descriptor =>
                {
                    Assert.Equal(
                        FakeGatewayTransportEndpointManager.ExternalEndpoint,
                        descriptor.Metadata["transport.endpoint"]);
                    Assert.Equal(
                        "preserved-existing-capacity-descriptor",
                        descriptor.Metadata["transport.endpoint.source"]);
                    Assert.Equal(
                        "gateway-port-forward",
                        descriptor.Metadata[
                            "kubernetes.transport.endpoint.source"]);
                    Assert.Equal(
                        "x-ai-runtime-instance-id",
                        descriptor.Metadata["gateway.routing.header"]);
                    Assert.Equal(
                        descriptor.RuntimeInstanceId,
                        descriptor.Metadata["gateway.routing.value"]);
                    Assert.Equal(
                        backendServiceName,
                        descriptor.Metadata["kubernetes.runtime.service.name"]);
                    Assert.StartsWith(
                        "http://127.0.0.1:",
                        descriptor.Metadata["transport.endpoint.internal"]);
                });
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
                    client,
                    new RuntimePoolCapacityStore(client));

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
                    client,
                    new RuntimePoolCapacityStore(client));

            var result =
                await strategy.StartAsync(
                    CreateRequest());

            Assert.False(result.Success);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(1, client.DeleteCallCount);
        }

        /// <summary>
        /// Verifies transport-ready runtimes remain valid startup evidence while all run slots
        /// are occupied and therefore do not trigger deletion of a healthy Pool Pod.
        /// </summary>
        [Fact]
        public async Task StartAsync_Should_Accept_Ready_Busy_RuntimeMembership_Without_Deleting_Pod()
        {
            var poolOptions = CreatePoolOptions();
            var hostOptions = CreateHostOptions();
            var client =
                new FakeAiKubernetesRuntimePoolHostClient();
            var capacityStore =
                new RuntimePoolCapacityStore(
                    client,
                    canAcceptRunAfterProjectedHeartbeat: false);

            var strategy =
                CreateStrategy(
                    poolOptions,
                    hostOptions,
                    client,
                    capacityStore);

            var result =
                await strategy.StartAsync(
                    CreateRequest());

            Assert.True(result.Success);
            Assert.Equal(1, client.CreateCallCount);
            Assert.Equal(1, client.ReadinessCallCount);
            Assert.Equal(0, client.DeleteCallCount);

            var descriptors =
                await capacityStore.ListAsync();

            Assert.Equal(3, descriptors.Count);
            Assert.All(
                descriptors,
                descriptor =>
                {
                    Assert.Equal(
                        AiRuntimeInstanceStatus.Ready,
                        descriptor.Status);
                    Assert.False(descriptor.CanAcceptRun);
                    Assert.Equal(
                        result.TransportEndpoint,
                        descriptor.Metadata["transport.endpoint"]);
                    Assert.Equal(
                        "control-plane",
                        descriptor.Metadata["transport.endpoint.scope"]);
                });
        }

        /// <summary>
        /// Creates the strategy under test.
        /// </summary>
        private static KubernetesAiRuntimePoolHostCreationStrategy
            CreateStrategy(
                AiKubernetesRuntimePoolOptions poolOptions,
                AiKubernetesRuntimePoolHostOptions hostOptions,
                FakeAiKubernetesRuntimePoolHostClient client,
                IAiRuntimeInstanceCapacityStore capacityStore,
                IAiKubernetesRuntimeGatewayManager? gatewayManager = null,
                IAiKubernetesGatewayTransportEndpointManager?
                    gatewayTransportEndpointManager = null)
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
                    .Instance,
                capacityStore,
                gatewayManager,
                gatewayTransportEndpointManager);
        }

        /// <summary>
        /// Materializes child descriptors when the fake Pod reports readiness and records projections.
        /// </summary>
        private sealed class RuntimePoolCapacityStore :
            IAiRuntimeInstanceCapacityStore
        {
            private readonly FakeAiKubernetesRuntimePoolHostClient client;
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor>
                descriptors = new(StringComparer.Ordinal);
            private readonly HashSet<string> pendingHeartbeats =
                new(StringComparer.Ordinal);
            private readonly bool canAcceptRunAfterProjectedHeartbeat;

            public RuntimePoolCapacityStore(
                FakeAiKubernetesRuntimePoolHostClient client,
                bool canAcceptRunAfterProjectedHeartbeat = true)
            {
                this.client = client;
                this.canAcceptRunAfterProjectedHeartbeat =
                    canAcceptRunAfterProjectedHeartbeat;
            }

            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.descriptors[descriptor.RuntimeInstanceId] = descriptor;

                if (descriptor.Metadata.TryGetValue(
                        "transport.endpoint.source",
                        out var endpointSource) &&
                    (string.Equals(
                         endpointSource,
                         "kubernetes-pool-service",
                         StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         endpointSource,
                         "kubernetes-pool-gateway",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    this.pendingHeartbeats.Add(
                        descriptor.RuntimeInstanceId);
                }

                return Task.CompletedTask;
            }

            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.descriptors.TryGetValue(
                        runtimeInstanceId,
                        out var existing))
                {
                    if (this.pendingHeartbeats.Remove(runtimeInstanceId))
                    {
                        existing =
                            CreatePreservingHeartbeatDescriptor(
                                existing,
                                this.canAcceptRunAfterProjectedHeartbeat);
                        this.descriptors[runtimeInstanceId] = existing;
                    }

                    return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                        existing);
                }

                var podSpec = this.client.LastCreatedPodSpec;
                var runtime =
                    podSpec?.Bootstrap.RuntimeInstances.FirstOrDefault(item =>
                        string.Equals(
                            item.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.Ordinal));

                if (podSpec is null || runtime is null)
                {
                    return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                        null);
                }

                var descriptor =
                    new AiRuntimeInstanceCapacityDescriptor
                    {
                        RuntimeInstanceId = runtime.RuntimeInstanceId,
                        PoolId = podSpec.PoolId,
                        HostId = string.Concat(
                            "fake-pod-uid-",
                            podSpec.PodRequestId),
                        ProviderName = runtime.ProviderName,
                        Status = AiRuntimeInstanceStatus.Ready,
                        WorkerCount = 3,
                        AvailableWorkerCount = 3,
                        MaxConcurrentRuns = 3,
                        MaxRunSlots = 3,
                        AvailableRunSlots = 0,
                        EffectiveAvailableRunSlots = 0,
                        CanAcceptRun = false,
                        LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["provider.name"] = runtime.ProviderName,
                            ["transport.name"] = runtime.TransportName,
                            ["transport.endpoint"] = string.Concat(
                                "http://127.0.0.1:",
                                runtime.TransportPort),
                            ["host.provider"] = "kubernetes",
                            ["host.creation.mode"] = "KubernetesPool",
                            ["hostType"] =
                                "runtime-instance-kubernetes-pool",
                            ["deployment"] = "kubernetes-pool"
                        }
                    };

                this.descriptors[runtimeInstanceId] = descriptor;

                return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                    descriptor);
            }

            private static AiRuntimeInstanceCapacityDescriptor
                CreatePreservingHeartbeatDescriptor(
                    AiRuntimeInstanceCapacityDescriptor descriptor,
                    bool canAcceptRun)
            {
                var metadata =
                    new Dictionary<string, string>(
                        descriptor.Metadata,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["transport.endpoint.source"] =
                            "preserved-existing-capacity-descriptor",
                        ["transport.endpoint.scope"] =
                            "control-plane"
                    };

                return new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = descriptor.RuntimeInstanceId,
                    PoolId = descriptor.PoolId,
                    HostId = descriptor.HostId,
                    TenantId = descriptor.TenantId,
                    TenantGroupId = descriptor.TenantGroupId,
                    ProviderName = descriptor.ProviderName,
                    IsolationMode = descriptor.IsolationMode,
                    AllowSharedFallback = descriptor.AllowSharedFallback,
                    PreferDedicatedCapacity = descriptor.PreferDedicatedCapacity,
                    Role = descriptor.Role,
                    Status = descriptor.Status,
                    WorkerCount = descriptor.WorkerCount,
                    ActiveWorkerCount = descriptor.ActiveWorkerCount,
                    AvailableWorkerCount = descriptor.AvailableWorkerCount,
                    MaxWorkersPerRun = descriptor.MaxWorkersPerRun,
                    MinWorkersRequiredPerRun =
                        descriptor.MinWorkersRequiredPerRun,
                    QueuedRunCount = descriptor.QueuedRunCount,
                    RunningRunCount = descriptor.RunningRunCount,
                    ActiveRunCount = descriptor.ActiveRunCount,
                    MaxConcurrentRuns = descriptor.MaxConcurrentRuns,
                    MaxRunSlots = descriptor.MaxRunSlots,
                    AvailableRunSlots =
                        canAcceptRun
                            ? descriptor.MaxRunSlots
                            : 0,
                    ReservedRunSlots = descriptor.ReservedRunSlots,
                    EffectiveAvailableRunSlots =
                        canAcceptRun
                            ? descriptor.MaxRunSlots
                            : 0,
                    IsQueuePaused = descriptor.IsQueuePaused,
                    CanAcceptRun = canAcceptRun,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = metadata,
                    ControlPlaneHostId = descriptor.ControlPlaneHostId,
                    ControlPlaneId = descriptor.ControlPlaneId
                };
            }

            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>
                ListAsync(
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                    this.descriptors
                        .Values
                        .OrderBy(
                            item => item.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(result);
            }

            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(
                    this.descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Records exact routes created for the shared Runtime Pool Service.
        /// </summary>
        private sealed class RecordingGatewayManager :
            IAiKubernetesRuntimeGatewayManager
        {
            public IList<AiKubernetesRuntimeRouteResult> HttpRoutes { get; } =
                new List<AiKubernetesRuntimeRouteResult>();

            public IList<AiKubernetesRuntimeRouteResult> GrpcRoutes { get; } =
                new List<AiKubernetesRuntimeRouteResult>();

            public Task<AiKubernetesGatewayEndpoint> EnsureGatewayAsync(
                string controlPlaneId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiKubernetesGatewayEndpoint
                    {
                        Namespace = "runtime-tests",
                        GatewayName = "runtime-gateway",
                        GatewayClassName = "runtime-gateway-class",
                        ListenerName = "runtime",
                        ListenerPort = 8080,
                        ServiceName = "runtime-gateway-service",
                        ServiceNamespace = "runtime-tests",
                        ServicePort = 8080,
                        InternalEndpoint =
                            "http://runtime-gateway-service.runtime-tests.svc.cluster.local:8080"
                    });
            }

            public Task<AiKubernetesRuntimeRouteResult> EnsureHttpRouteAsync(
                string controlPlaneId,
                string runtimeInstanceId,
                string runtimeServiceName,
                int backendPort,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var route =
                    CreateRoute(
                        runtimeInstanceId,
                        runtimeServiceName,
                        backendPort,
                        AiKubernetesRuntimeRouteKind.HttpRoute);

                this.HttpRoutes.Add(route);
                return Task.FromResult(route);
            }

            public Task<AiKubernetesRuntimeRouteResult> EnsureGrpcRouteAsync(
                string controlPlaneId,
                string runtimeInstanceId,
                string runtimeServiceName,
                int backendPort,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var route =
                    CreateRoute(
                        runtimeInstanceId,
                        runtimeServiceName,
                        backendPort,
                        AiKubernetesRuntimeRouteKind.GrpcRoute);

                this.GrpcRoutes.Add(route);
                return Task.FromResult(route);
            }

            public Task DeleteRuntimeRouteAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            private static AiKubernetesRuntimeRouteResult CreateRoute(
                string runtimeInstanceId,
                string runtimeServiceName,
                int backendPort,
                AiKubernetesRuntimeRouteKind routeKind)
            {
                return new AiKubernetesRuntimeRouteResult
                {
                    Namespace = "runtime-tests",
                    GatewayName = "runtime-gateway",
                    ListenerName = "runtime",
                    RouteName = string.Concat(
                        "route-",
                        runtimeInstanceId),
                    RouteKind = routeKind,
                    RuntimeInstanceId = runtimeInstanceId,
                    RuntimeServiceName = runtimeServiceName,
                    BackendPort = backendPort,
                    RoutingHeaderName = "x-ai-runtime-instance-id",
                    RoutingHeaderValue = runtimeInstanceId
                };
            }
        }

        /// <summary>
        /// Returns one process-local endpoint representing the existing shared Gateway port-forward.
        /// </summary>
        private sealed class FakeGatewayTransportEndpointManager :
            IAiKubernetesGatewayTransportEndpointManager
        {
            public const string ExternalEndpoint =
                "http://127.0.0.1:32123";

            public Task<AiKubernetesGatewayTransportEndpoint> ResolveAsync(
                AiKubernetesGatewayEndpoint gatewayEndpoint,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiKubernetesGatewayTransportEndpoint
                    {
                        Endpoint = ExternalEndpoint,
                        InternalEndpoint = gatewayEndpoint.InternalEndpoint,
                        Namespace = gatewayEndpoint.Namespace,
                        GatewayName = gatewayEndpoint.GatewayName,
                        ServiceName = gatewayEndpoint.ServiceName,
                        ServicePort = gatewayEndpoint.ServicePort,
                        UsesPortForward = true,
                        LocalPort = 32123
                    });
            }

            public void Dispose()
            {
            }
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
                StartupTimeout = TimeSpan.FromSeconds(1),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(1),
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
