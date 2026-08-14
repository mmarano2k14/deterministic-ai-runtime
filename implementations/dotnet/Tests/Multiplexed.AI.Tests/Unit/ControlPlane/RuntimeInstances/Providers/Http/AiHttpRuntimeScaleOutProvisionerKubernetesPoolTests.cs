using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http.ScaleOut;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Verifies HTTP KubernetesPool scale-out parity with the canonical Runtime Pool Pod creation authority.
    /// </summary>
    public sealed class AiHttpRuntimeScaleOutProvisionerKubernetesPoolTests
    {
        /// <summary>
        /// Verifies that HTTP KubernetesPool provisioning delegates physical Pod creation
        /// to the canonical Pod creation executor instead of using direct host-manager startup.
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
                    ExpectedPoolId = "http-pool-executor",
                    Result =
                        new AiRuntimePoolPodCreationResult
                        {
                            RequestId = "http-kubernetes-pool-request-1",
                            PoolId = "http-pool-executor",
                            HostRequestId = "http-kubernetes-pool-host-request-1",
                            PrimaryRuntimeInstanceId = "http-runtime-pool-primary",
                            PodUid = "http-pod-uid-executor",
                            Status = AiRuntimePoolPodCreationStatus.Created,
                            RuntimeInstanceIds =
                                new[]
                                {
                                    "http-runtime-pool-primary",
                                    "http-runtime-pool-secondary"
                                }
                        }
                };

            var options = CreateOptions();
            options.PoolId = "http-pool-executor";

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager,
                    options,
                    podCreationExecutor);

            var result =
                await provisioner
                    .ProvisionAsync(
                        CreateRequest(
                            "http-kubernetes-pool-request-1"))
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal(1, podCreationExecutor.CallCount);
            Assert.Empty(hostManager.StartRequests);
            Assert.Equal(
                "http-runtime-pool-primary",
                result.RuntimeInstanceId);
            Assert.Equal(
                AiRuntimePoolPodCreationStatus.Created.ToString(),
                result.Metadata["runtime.pool.podCreation.status"]);
        }

        /// <summary>
        /// Verifies that HTTP KubernetesPool scale-out rejects missing logical PoolId authority.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Reject_When_KubernetesPool_PoolId_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = new FakeRuntimeHostManager();
            var options = CreateOptions();

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager,
                    options,
                    runtimePoolPodCreationExecutor: null);

            var result =
                await provisioner
                    .ProvisionAsync(
                        CreateRequest(
                            "http-kubernetes-pool-missing-id"))
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Equal(
                "http-runtime-scaleout-kubernetes-pool-id-missing",
                result.FailureReason);
            Assert.Empty(hostManager.StartRequests);
        }

        /// <summary>
        /// Verifies that HTTP KubernetesPool can never bypass the canonical Pod creation executor.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Fail_When_KubernetesPool_PodCreationExecutor_Is_Missing()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = new FakeRuntimeHostManager();
            var options = CreateOptions();
            options.PoolId = "http-pool-missing-executor";

            var provisioner =
                CreateProvisioner(
                    registry,
                    capacityStore,
                    hostManager,
                    options,
                    runtimePoolPodCreationExecutor: null);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => provisioner.ProvisionAsync(
                        CreateRequest(
                            "http-kubernetes-pool-missing-executor")));

            Assert.Contains(
                "IAiRuntimePoolPodCreationExecutor",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(hostManager.StartRequests);
        }

        /// <summary>
        /// Verifies that HTTP KubernetesPool preserves an explicit zero local queue capacity.
        /// </summary>
        [Fact]
        public async Task ProvisionAsync_Should_Preserve_Zero_LocalQueueCapacity_For_KubernetesPool()
        {
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();
            var hostManager = new FakeRuntimeHostManager();
            var tenantSettingsProvider =
                new FakeTenantRuntimeSettingsProvider
                {
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 0,
                    RuntimeInstanceIdPrefix = "http-runtime-pool"
                };

            var podCreationExecutor =
                new FakeRuntimePoolPodCreationExecutor
                {
                    ExpectedPoolId = "http-pool-zero-queue",
                    Result =
                        new AiRuntimePoolPodCreationResult
                        {
                            RequestId = "http-kubernetes-pool-zero-queue",
                            PoolId = "http-pool-zero-queue",
                            HostRequestId = "http-zero-queue-host-request",
                            PrimaryRuntimeInstanceId = "http-zero-queue-runtime",
                            Status = AiRuntimePoolPodCreationStatus.Created,
                            RuntimeInstanceIds =
                                new[]
                                {
                                    "http-zero-queue-runtime"
                                }
                        }
                };

            var options = CreateOptions();
            options.PoolId = "http-pool-zero-queue";

            var provisioner =
                new AiHttpRuntimeScaleOutProvisioner(
                    registry,
                    capacityStore,
                    hostManager,
                    new FakeRuntimeInstanceReadinessWaiter(),
                    tenantSettingsProvider,
                    Options.Create(options),
                    NullLogger<AiHttpRuntimeScaleOutProvisioner>.Instance,
                    runtimeHostProcessControl: null,
                    runtimePoolPodCreationExecutor:
                        podCreationExecutor);

            var request =
                CreateRequest(
                    "http-kubernetes-pool-zero-queue");

            request.LocalQueueCapacity = 0;

            var result =
                await provisioner
                    .ProvisionAsync(request)
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Empty(hostManager.StartRequests);
            Assert.Equal(1, podCreationExecutor.CallCount);
            Assert.NotNull(podCreationExecutor.LastRequest);
            Assert.Equal(
                0,
                podCreationExecutor.LastRequest!.LocalQueueCapacity);
        }

        private static AiHttpRuntimeScaleOutProvisioner CreateProvisioner(
            FakeRuntimeInstanceRegistry registry,
            FakeRuntimeInstanceCapacityStore capacityStore,
            FakeRuntimeHostManager hostManager,
            AiHttpRuntimeScaleOutOptions options,
            IAiRuntimePoolPodCreationExecutor? runtimePoolPodCreationExecutor)
        {
            var tenantSettingsProvider =
                new FakeTenantRuntimeSettingsProvider
                {
                    WorkerCountPerInstance = 2,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 40,
                    RuntimeInstanceIdPrefix = "http-runtime-pool"
                };

            return new AiHttpRuntimeScaleOutProvisioner(
                registry,
                capacityStore,
                hostManager,
                new FakeRuntimeInstanceReadinessWaiter(),
                tenantSettingsProvider,
                Options.Create(options),
                NullLogger<AiHttpRuntimeScaleOutProvisioner>.Instance,
                runtimeHostProcessControl: null,
                runtimePoolPodCreationExecutor:
                    runtimePoolPodCreationExecutor);
        }

        private static AiHttpRuntimeScaleOutOptions CreateOptions()
        {
            return new AiHttpRuntimeScaleOutOptions
            {
                Enabled = true,
                Mode = AiHttpRuntimeScaleOutModes.HostManager,
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                CapacityTopologyMode =
                    Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity
                        .AiRuntimeCapacityTopologyMode.KubernetesPool,
                RequireReadiness = false,
                EndpointTemplate = "http://127.0.0.1",
                DefaultRuntimeInstanceIdPrefix = "http-runtime-pool"
            };
        }

        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = "http-control-plane",
                SharedRunId = "http-shared-run",
                TenantId = "http-tenant",
                TenantGroupId = "http-group",
                RequestedTargetInstanceCount = 1,
                CurrentInstanceCount = 0,
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 3,
                LocalQueueCapacity = 40,
                PreferDedicatedCapacity = false,
                AllowSharedFallback = true,
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create()
            };
        }

        private sealed class FakeRuntimePoolPodCreationExecutor :
            IAiRuntimePoolPodCreationExecutor
        {
            public int CallCount { get; private set; }

            public string ExpectedPoolId { get; init; } =
                "http-pool-executor";

            public AiRuntimeScaleOutProviderRequest? LastRequest
            {
                get;
                private set;
            }

            public required AiRuntimePoolPodCreationResult Result
            {
                get;
                init;
            }

            public Task<AiRuntimePoolPodCreationResult> ExecuteAsync(
                AiRuntimeScaleOutProviderRequest request,
                AiRuntimeCapacitySelectionCandidate candidate,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CallCount++;
                LastRequest = request;

                Assert.Equal(
                    AiRuntimeCapacitySelectionLevel.RuntimePoolPodCreation,
                    candidate.Level);
                Assert.Equal(
                    ExpectedPoolId,
                    candidate.PoolId);
                Assert.Equal(
                    "http",
                    candidate.ProviderName);

                return Task.FromResult(Result);
            }
        }
    }
}
