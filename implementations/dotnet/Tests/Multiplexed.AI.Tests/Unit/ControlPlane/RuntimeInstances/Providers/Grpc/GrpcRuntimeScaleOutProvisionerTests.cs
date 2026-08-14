using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Grpc
{
    /// <summary>
    /// Unit tests for <see cref="AiGrpcRuntimeScaleOutProvisioner"/>.
    /// </summary>
    public sealed class GrpcRuntimeScaleOutProvisionerTests
    {
        private const string ControlPlaneId = "control-plane-test-1";
        private const string TenantId = "tenant-a";
        private const string TenantGroupId = "group-a";
        private const string SharedRunId = "shared-run-test-1";
        private const string RuntimeInstancePrefix = "grpc-runtime";
        private const string RuntimeInstanceId = "control-plane-test-1:grpc-runtime-1";
        private const string RuntimeEndpoint = "http://127.0.0.1:50051/control-plane-test-1:grpc-runtime-1";
        private const string EndpointTemplate = "http://127.0.0.1:50051/{runtimeInstanceId}";

        

        /// <summary>
        /// Verifies that gRPC scale-out rejects the request when scale-out is disabled.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_ScaleOut_Is_Disabled()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var provisioner = CreateProvisioner(registry, capacityStore, options: CreateOptions(AiGrpcRuntimeScaleOutModes.MetadataOnly, enabled: false));

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-scaleout-disabled-request-1"));

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Null(result.RuntimeInstanceId);
            Assert.Equal("grpc-runtime-scaleout-disabled", result.FailureReason);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Verifies that gRPC scale-out rejects the request when the request id is missing.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_RequestId_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var provisioner = CreateProvisioner(registry, capacityStore);

            var result = await provisioner.ProvisionAsync(CreateRequest(string.Empty));

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Null(result.RuntimeInstanceId);
            Assert.Equal("grpc-runtime-scaleout-request-id-missing", result.FailureReason);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Verifies that gRPC scale-out rejects the request when the control-plane id is missing.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_ControlPlaneId_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var provisioner = CreateProvisioner(registry, capacityStore);

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-scaleout-request-1", controlPlaneId: string.Empty));

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Null(result.RuntimeInstanceId);
            Assert.Equal("grpc-runtime-scaleout-control-plane-id-missing", result.FailureReason);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Verifies that gRPC scale-out uses tenant runtime settings before request sizing values.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Use_Tenant_Settings_Before_Request_Sizing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var tenantSettingsProvider = new FakeTenantRuntimeSettingsProvider
            {
                WorkerCountPerInstance = 4,
                MaxConcurrentRunsPerInstance = 5,
                LocalQueueCapacity = 60,
                RuntimeInstanceIdPrefix = "tenant-grpc-runtime"
            };

            var provisioner = CreateProvisioner(registry, capacityStore, tenantSettingsProvider: tenantSettingsProvider);

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-scaleout-tenant-settings-request-1", requestedTargetInstanceCount: 2, currentInstanceCount: 1));

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal("control-plane-test-1:tenant-grpc-runtime-2", result.RuntimeInstanceId);
            Assert.Single(registry.RuntimeInstances);
            Assert.Single(capacityStore.PublishedDescriptors);

            var registration = registry.RuntimeInstances.Single();
            var descriptor = capacityStore.PublishedDescriptors.Single();

            Assert.Equal("control-plane-test-1:tenant-grpc-runtime-2", registration.RuntimeInstanceId);
            Assert.Equal("control-plane-test-1:tenant-grpc-runtime-2", descriptor.RuntimeInstanceId);
            Assert.Equal(4, registration.WorkerCount);
            Assert.Equal(4, descriptor.WorkerCount);
            Assert.Equal(5, registration.MaxConcurrentRuns);
            Assert.Equal(5, descriptor.MaxConcurrentRuns);
            Assert.Equal(5, descriptor.AvailableRunSlots);
            Assert.Equal("http://127.0.0.1:50051/control-plane-test-1:tenant-grpc-runtime-2", descriptor.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint]);
            Assert.Equal("tenant-grpc-runtime", descriptor.Metadata["runtime.instanceIdPrefix"]);
            Assert.Equal("4", descriptor.Metadata["runtime.workerCountPerInstance"]);
            Assert.Equal("5", descriptor.Metadata["runtime.maxConcurrentRunsPerInstance"]);
            Assert.Equal("60", descriptor.Metadata["runtime.localQueueCapacity"]);
        }

        /// <summary>
        /// Verifies that gRPC scale-out delegates runtime creation to the host manager when host-manager mode is enabled.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Start_Runtime_With_HostManager_When_HostManager_Mode()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = CreateSuccessfulHostManager();
            var provisioner = CreateProvisioner(registry, capacityStore, hostManager: hostManager, options: CreateOptions(AiGrpcRuntimeScaleOutModes.HostManager, requireReadiness: false));

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-host-manager-scaleout-request-1"));

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(RuntimeInstanceId, result.RuntimeInstanceId);
            Assert.Single(hostManager.StartRequests);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);

            var startRequest = hostManager.StartRequests.Single();

            Assert.Equal("grpc-host-manager-scaleout-request-1", startRequest.RequestId);
            Assert.Equal(ControlPlaneId, startRequest.ControlPlaneId);
            Assert.Equal(RuntimeInstanceId, startRequest.RuntimeInstanceId);
            Assert.Equal(RuntimeInstancePrefix, startRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(AiGrpcRuntimeProviderConstants.ProviderName, startRequest.ProviderName);
            Assert.Equal(AiGrpcRuntimeProviderConstants.TransportName, startRequest.TransportName);
            Assert.Equal(RuntimeEndpoint, startRequest.TransportEndpoint);
            Assert.Equal(TenantId, startRequest.TenantId);
            Assert.Equal(TenantGroupId, startRequest.TenantGroupId);
            Assert.Equal(AiRuntimeHostCreationMode.Fixture, startRequest.HostCreationMode);
            Assert.Equal(2, startRequest.WorkerCountPerInstance);
            Assert.Equal(3, startRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(40, startRequest.LocalQueueCapacity);
        }

        /// <summary>
        /// Verifies that gRPC scale-out rejects the request when the host manager fails to start the runtime.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_HostManager_Start_Fails()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = new FakeRuntimeHostManager
            {
                Result = new AiRuntimeHostStartResult
                {
                    Success = false,
                    RuntimeInstanceId = RuntimeInstanceId,
                    FailureReason = "fake-host-start-failed",
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                }
            };

            var provisioner = CreateProvisioner(registry, capacityStore, hostManager: hostManager, options: CreateOptions(AiGrpcRuntimeScaleOutModes.HostManager, requireReadiness: false));

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-host-manager-failed-request-1"));

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Null(result.RuntimeInstanceId);
            Assert.Equal("fake-host-start-failed", result.FailureReason);
            Assert.Single(hostManager.StartRequests);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Verifies that gRPC scale-out waits for runtime readiness when readiness is required.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Wait_For_Readiness_When_HostManager_Mode_Requires_Readiness()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var readinessWaiter = new FakeRuntimeInstanceReadinessWaiter();
            var hostManager = CreateSuccessfulHostManager();
            var provisioner = CreateProvisioner(registry, capacityStore, hostManager: hostManager, readinessWaiter: readinessWaiter, options: CreateOptions(AiGrpcRuntimeScaleOutModes.HostManager, requireReadiness: true));

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-host-manager-readiness-request-1"));

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(RuntimeInstanceId, result.RuntimeInstanceId);
            Assert.Single(hostManager.StartRequests);
            Assert.Single(readinessWaiter.Requests);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);

            var readinessRequest = readinessWaiter.Requests.Single();

            Assert.Equal(ControlPlaneId, readinessRequest.ControlPlaneId);
            Assert.Equal(RuntimeInstanceId, readinessRequest.RuntimeInstanceId);
            Assert.Equal(AiGrpcRuntimeProviderConstants.ProviderName, readinessRequest.ProviderName);
            Assert.Equal(AiGrpcRuntimeProviderConstants.TransportName, readinessRequest.TransportName);
            Assert.Equal(RuntimeEndpoint, readinessRequest.TransportEndpoint);
        }

        /// <summary>
        /// Verifies that gRPC scale-out rejects the request when runtime readiness fails.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_HostManager_Readiness_Fails()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var readinessWaiter = new FakeRuntimeInstanceReadinessWaiter
            {
                Result = new AiRuntimeInstanceReadinessResult
                {
                    Success = false,
                    FailureReason = "fake-readiness-failed",
                    TimedOut = true,
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                }
            };

            var hostManager = CreateSuccessfulHostManager();
            var provisioner = CreateProvisioner(registry, capacityStore, hostManager: hostManager, readinessWaiter: readinessWaiter, options: CreateOptions(AiGrpcRuntimeScaleOutModes.HostManager, requireReadiness: true));

            var result = await provisioner.ProvisionAsync(CreateRequest("grpc-host-manager-readiness-failed-request-1"));

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Null(result.RuntimeInstanceId);
            Assert.Equal("fake-readiness-failed", result.FailureReason);
            Assert.Single(hostManager.StartRequests);
            Assert.Single(readinessWaiter.Requests);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);
        }

        /// <summary>
        /// Verifies that gRPC scale-out can delegate lifecycle creation to Kubernetes while preserving gRPC as the runtime transport.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Start_Kubernetes_Host_Manager_With_Grpc_Runtime_Transport()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager =
                new FakeRuntimeHostManager
                {
                    Result = new AiRuntimeHostStartResult
                    {
                        Success = true,
                        RuntimeInstanceId = RuntimeInstanceId,
                        TransportEndpoint = RuntimeEndpoint,
                        ProviderName = AiGrpcRuntimeProviderConstants.ProviderName,
                        TransportName = AiGrpcRuntimeProviderConstants.TransportName,
                        Metadata =
                            new Dictionary<string, string>
                            {
                                [AiRuntimeHostMetadataKeys.HostProvider] = AiRuntimeHostProviderNames.Kubernetes,
                                [AiRuntimeHostMetadataKeys.HostCreationMode] = AiRuntimeHostCreationMode.Kubernetes.ToString(),
                                [AiRuntimeHostMetadataKeys.HostCreationStrategy] = "KubernetesAiRuntimeHostCreationStrategy"
                            },
                        ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                    }
                };

            var options =
                CreateOptions(
                    AiGrpcRuntimeScaleOutModes.HostManager,
                    requireReadiness: false);

            options.HostCreationMode = AiRuntimeHostCreationMode.Kubernetes;

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager: hostManager,
                    options: options);

            var result =
                await provisioner.ProvisionAsync(
                    CreateRequest("grpc-kubernetes-host-manager-scaleout-request-1"));

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(RuntimeInstanceId, result.RuntimeInstanceId);
            Assert.Single(hostManager.StartRequests);
            Assert.Empty(registry.RuntimeInstances);
            Assert.Empty(capacityStore.PublishedDescriptors);

            var startRequest =
                hostManager.StartRequests.Single();

            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes, startRequest.HostCreationMode);
            Assert.Equal(AiGrpcRuntimeProviderConstants.ProviderName, startRequest.ProviderName);
            Assert.Equal(AiGrpcRuntimeProviderConstants.TransportName, startRequest.TransportName);
            Assert.Equal(RuntimeEndpoint, startRequest.TransportEndpoint);
            Assert.Equal(TenantId, startRequest.TenantId);
            Assert.Equal(TenantGroupId, startRequest.TenantGroupId);
            Assert.Equal(2, startRequest.WorkerCountPerInstance);
            Assert.Equal(3, startRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(40, startRequest.LocalQueueCapacity);

            Assert.Equal(AiGrpcRuntimeProviderConstants.ProviderName, result.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
            Assert.Equal(AiGrpcRuntimeProviderConstants.TransportName, result.Metadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]);
            Assert.Equal(AiRuntimeHostProviderNames.Kubernetes, result.Metadata[AiRuntimeHostMetadataKeys.HostProvider]);
            Assert.Equal(AiRuntimeHostCreationMode.Kubernetes.ToString(), result.Metadata[AiRuntimeHostMetadataKeys.HostCreationMode]);
            Assert.NotEqual("kubernetes", startRequest.ProviderName);
            Assert.NotEqual("kubernetes", result.Metadata[AiRuntimeInstanceProviderMetadataKeys.ProviderName]);
        }

        /// <summary>
        /// Verifies that gRPC KubernetesPool provisioning delegates physical Pod
        /// creation to the canonical Pod creation executor.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Delegate_KubernetesPool_To_PodCreation_Executor()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = new FakeRuntimeHostManager();
            var podCreationExecutor =
                new FakeRuntimePoolPodCreationExecutor
                {
                    Result =
                        new AiRuntimePoolPodCreationResult
                        {
                            RequestId =
                                "grpc-kubernetes-pool-executor-request-1",
                            PoolId = "pool-executor",
                            HostRequestId =
                                "kubernetes-runtime-pool-pod-scale-out-test",
                            PrimaryRuntimeInstanceId =
                                "runtime-pool-executor-primary",
                            PodUid = "pod-uid-executor",
                            Status =
                                AiRuntimePoolPodCreationStatus.Created,
                            RuntimeInstanceIds =
                                new[]
                                {
                                    "runtime-pool-executor-primary",
                                    "runtime-pool-executor-secondary"
                                }
                        }
                };

            var options =
                CreateOptions(
                    AiGrpcRuntimeScaleOutModes.HostManager,
                    requireReadiness: false);

            options.HostCreationMode =
                AiRuntimeHostCreationMode.KubernetesPool;
            options.PoolId = "pool-executor";

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager: hostManager,
                    options: options,
                    runtimePoolPodCreationExecutor:
                        podCreationExecutor);

            var result =
                await provisioner.ProvisionAsync(
                    CreateRequest(
                        "grpc-kubernetes-pool-executor-request-1"));

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(1, podCreationExecutor.CallCount);
            Assert.Empty(hostManager.StartRequests);
            Assert.Equal(
                "runtime-pool-executor-primary",
                result.RuntimeInstanceId);
            Assert.Equal(
                AiRuntimePoolPodCreationStatus.Created.ToString(),
                result.Metadata[
                    "runtime.pool.podCreation.status"]);
        }

        /// <summary>
        /// Verifies that Kubernetes Runtime Pool scale-out preserves an explicit
        /// zero-capacity local queue instead of replacing it with the provider default.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Preserve_Zero_LocalQueueCapacity_For_KubernetesPool()
        {
            var registry =
                new FakeRuntimeInstanceRegistry();

            var capacityStore =
                new FakeRuntimeInstanceCapacityStore();

            var hostManager =
                CreateSuccessfulHostManager();

            var tenantSettingsProvider =
                new FakeTenantRuntimeSettingsProvider
                {
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    RuntimeInstanceIdPrefix =
                        RuntimeInstancePrefix
                };

            var options =
                CreateOptions(
                    AiGrpcRuntimeScaleOutModes.HostManager,
                    requireReadiness: false);

            options.HostCreationMode =
                AiRuntimeHostCreationMode.KubernetesPool;
            options.PoolId =
                "pool-zero-local-queue";

            var podCreationExecutor =
                new FakeRuntimePoolPodCreationExecutor
                {
                    ExpectedPoolId =
                        "pool-zero-local-queue",
                    Result =
                        new AiRuntimePoolPodCreationResult
                        {
                            RequestId =
                                "grpc-kubernetes-pool-zero-queue-request-1",
                            PoolId =
                                "pool-zero-local-queue",
                            HostRequestId =
                                "zero-queue-host-request",
                            PrimaryRuntimeInstanceId =
                                "zero-queue-runtime",
                            Status =
                                AiRuntimePoolPodCreationStatus.Created,
                            RuntimeInstanceIds =
                                new[]
                                {
                                    "zero-queue-runtime"
                                }
                        }
                };

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager: hostManager,
                    tenantSettingsProvider:
                        tenantSettingsProvider,
                    options: options,
                    runtimePoolPodCreationExecutor:
                        podCreationExecutor);

            var request =
                CreateRequest(
                    "grpc-kubernetes-pool-zero-queue-request-1");

            request.LocalQueueCapacity = 0;

            var result =
                await provisioner.ProvisionAsync(
                    request);

            Assert.True(result.Success);
            Assert.Empty(hostManager.StartRequests);
            Assert.Equal(1, podCreationExecutor.CallCount);
            Assert.NotNull(podCreationExecutor.LastRequest);
            Assert.Equal(
                0,
                podCreationExecutor.LastRequest!.LocalQueueCapacity);
        }

        /// <summary>
        /// Verifies that KubernetesPool can never bypass the canonical Pod creation
        /// executor through the direct host-manager path.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Fail_When_KubernetesPool_PodCreationExecutor_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = CreateSuccessfulHostManager();

            var options =
                CreateOptions(
                    AiGrpcRuntimeScaleOutModes.HostManager,
                    requireReadiness: false);

            options.HostCreationMode =
                AiRuntimeHostCreationMode.KubernetesPool;
            options.PoolId =
                "pool-missing-executor";

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager: hostManager,
                    options: options);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provisioner.ProvisionAsync(
                        CreateRequest(
                            "grpc-kubernetes-pool-missing-executor")));

            Assert.Contains(
                "IAiRuntimePoolPodCreationExecutor",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(hostManager.StartRequests);
        }

        /// <summary>
        /// Creates a gRPC runtime scale-out provisioner for tests.
        /// </summary>
        private static AiGrpcRuntimeScaleOutProvisioner CreateProvisioner(
            FakeRuntimeInstanceRegistry registry,
            FakeRuntimeInstanceCapacityStore capacityStore,
            FakeRuntimeHostManager? hostManager = null,
            FakeRuntimeInstanceReadinessWaiter? readinessWaiter = null,
            FakeTenantRuntimeSettingsProvider? tenantSettingsProvider = null,
            AiGrpcRuntimeScaleOutOptions? options = null,
            IAiRuntimePoolPodCreationExecutor?
                runtimePoolPodCreationExecutor = null)
        {
            tenantSettingsProvider ??= new FakeTenantRuntimeSettingsProvider
            {
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 40,
                RuntimeInstanceIdPrefix = RuntimeInstancePrefix
            };

            return new AiGrpcRuntimeScaleOutProvisioner(
                registry,
                capacityStore,
                hostManager ?? new FakeRuntimeHostManager(),
                readinessWaiter ?? new FakeRuntimeInstanceReadinessWaiter(),
                tenantSettingsProvider,
                Options.Create(options ?? CreateOptions(AiGrpcRuntimeScaleOutModes.MetadataOnly)),
                NullLogger<AiGrpcRuntimeScaleOutProvisioner>.Instance,
                runtimeHostProcessControl: null,
                runtimePoolPodCreationExecutor:
                    runtimePoolPodCreationExecutor);
        }

        /// <summary>
        /// Creates gRPC runtime scale-out options for tests.
        /// </summary>
        private static AiGrpcRuntimeScaleOutOptions CreateOptions(
            string mode,
            bool enabled = true,
            bool requireReadiness = false)
        {
            return new AiGrpcRuntimeScaleOutOptions
            {
                Enabled = enabled,
                Mode = mode,
                HostCreationMode = AiRuntimeHostCreationMode.Fixture,
                RequireReadiness = requireReadiness,
                ReadinessTimeoutSeconds = 5,
                ReadinessPollIntervalMilliseconds = 50,
                EndpointTemplate = EndpointTemplate,
                DefaultRuntimeInstanceIdPrefix = RuntimeInstancePrefix
            };
        }

        /// <summary>
        /// Creates a gRPC scale-out provider request for tests.
        /// </summary>
        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId,
            string controlPlaneId = ControlPlaneId,
            int requestedTargetInstanceCount = 1,
            int currentInstanceCount = 0)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = controlPlaneId,
                SharedRunId = SharedRunId,
                TenantId = TenantId,
                TenantGroupId = TenantGroupId,
                RequestedTargetInstanceCount = requestedTargetInstanceCount,
                CurrentInstanceCount = currentInstanceCount,
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 40,
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
            };
        }

        private sealed class FakeRuntimePoolPodCreationExecutor :
            IAiRuntimePoolPodCreationExecutor
        {
            public int CallCount { get; private set; }

            public string ExpectedPoolId { get; init; } =
                "pool-executor";

            public AiRuntimeScaleOutProviderRequest?
                LastRequest { get; private set; }

            public AiRuntimeCapacitySelectionCandidate?
                LastCandidate { get; private set; }

            public required AiRuntimePoolPodCreationResult Result
            {
                get; init;
            }

            public Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.CallCount++;
                this.LastRequest = request;
                this.LastCandidate = candidate;

                Assert.Equal(
                    AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation,
                    candidate.Level);
                Assert.Equal(
                    this.ExpectedPoolId,
                    candidate.PoolId);

                return Task.FromResult(this.Result);
            }
        }

        /// <summary>
        /// Creates a successful fake runtime host manager.
        /// </summary>
        private static FakeRuntimeHostManager CreateSuccessfulHostManager()
        {
            return new FakeRuntimeHostManager
            {
                Result = new AiRuntimeHostStartResult
                {
                    Success = true,
                    RuntimeInstanceId = RuntimeInstanceId,
                    TransportEndpoint = RuntimeEndpoint,
                    ProviderName = AiGrpcRuntimeProviderConstants.ProviderName,
                    TransportName = AiGrpcRuntimeProviderConstants.TransportName,
                    Metadata = new Dictionary<string, string>(),
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                }
            };
        }
    }
}