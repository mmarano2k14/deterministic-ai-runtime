using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Validates deterministic Step 7E Runtime Pool Pod creation.
    /// </summary>
    public sealed class AiRuntimePoolPodCreationExecutorTests
    {
        /// <summary>
        /// Verifies exact host strategy execution and ready membership convergence.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Create_One_Exact_Ready_Pod()
        {
            var strategy = new RecordingHostCreationStrategy();
            var membership = new ReadyMembershipEnumerator(strategy);
            var executor = CreateExecutor(strategy, membership);
            var request = CreateRequest("request-step-7e-create");
            request.TenantId = "stale-request-tenant";
            request.TenantGroupId =
                "stale-request-tenant-group";

            var result =
                await executor.ExecuteAsync(
                    request,
                    CreateCandidate());

            Assert.True(result.IsCreated);
            Assert.False(result.IsDeduplicated);
            Assert.Equal(1, strategy.CallCount);
            Assert.NotNull(strategy.LastRequest);
            Assert.Equal(
                AiRuntimeHostCreationMode.KubernetesPool,
                strategy.LastRequest!.HostCreationMode);
            Assert.Equal("pool-step-7e", strategy.LastRequest.PoolId);
            Assert.Equal(
                result.PrimaryRuntimeInstanceId,
                strategy.LastRequest.RuntimeInstanceId);
            Assert.Equal(
                "tenant-step-7e",
                strategy.LastRequest.TenantId);
            Assert.Equal(
                "tenant-group-step-7e",
                strategy.LastRequest.TenantGroupId);
            Assert.Equal("pod-uid-step-7e", result.PodUid);
            Assert.Equal(3, result.RuntimeInstanceIds.Count);
            Assert.Contains(
                result.PrimaryRuntimeInstanceId,
                result.RuntimeInstanceIds);
        }

        /// <summary>
        /// Verifies that hierarchical Pod creation uses the canonical runtime host manager
        /// boundary instead of invoking the KubernetesPool strategy directly.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Use_Runtime_Host_Manager_Boundary()
        {
            var strategy = new RecordingHostCreationStrategy();
            var membership = new ReadyMembershipEnumerator(strategy);
            var runtimeHostManager =
                new RecordingRuntimeHostManager(strategy);
            var executor =
                CreateExecutor(
                    strategy,
                    membership,
                    runtimeHostManager);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest("request-step-7e-host-manager"),
                    CreateCandidate());

            Assert.True(result.IsCreated);
            Assert.Equal(1, runtimeHostManager.CallCount);
            Assert.Equal(1, strategy.CallCount);
            Assert.Same(
                strategy.LastRequest,
                runtimeHostManager.LastRequest);
            Assert.Equal(
                result.PrimaryRuntimeInstanceId,
                runtimeHostManager.LastRequest!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that retrying the same request returns the prior exact result without
        /// creating another Pod.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Deduplicate_Same_Request()
        {
            var strategy = new RecordingHostCreationStrategy();
            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy));
            var request = CreateRequest("request-step-7e-dedup");
            var candidate = CreateCandidate();

            var first =
                await executor.ExecuteAsync(request, candidate);
            var second =
                await executor.ExecuteAsync(request, candidate);

            Assert.True(first.IsCreated);
            Assert.True(second.IsDeduplicated);
            Assert.Equal(first.PodUid, second.PodUid);
            Assert.Equal(
                first.PrimaryRuntimeInstanceId,
                second.PrimaryRuntimeInstanceId);
            Assert.Equal(1, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that rejected host creation is not cached and remains retryable.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Not_Cache_Rejected_Start()
        {
            var strategy =
                new RecordingHostCreationStrategy(
                    reject: true);
            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy));
            var request = CreateRequest("request-step-7e-retry");
            var candidate = CreateCandidate();

            var first =
                await executor.ExecuteAsync(request, candidate);
            var second =
                await executor.ExecuteAsync(request, candidate);

            Assert.Equal(
                AiRuntimePoolPodCreationStatus.Rejected,
                first.Status);
            Assert.True(first.Retryable);
            Assert.Equal(
                AiRuntimePoolPodCreationStatus.Rejected,
                second.Status);
            Assert.Equal(2, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that host or runtime identity cannot be smuggled into a new-Pod
        /// selection candidate.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Reject_Non_Pod_Candidate()
        {
            var strategy = new RecordingHostCreationStrategy();
            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy));
            var candidate = CreateCandidate();
            candidate.HostId = "existing-host";

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(
                    CreateRequest("request-step-7e-invalid"),
                    candidate));

            Assert.Equal(0, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that many concurrent executions of one logical Pod request invoke
        /// the Kubernetes host strategy exactly once and converge on the same Pod UID,
        /// primary runtime identity, and ready membership.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Deduplicate_Concurrent_Same_Request()
        {
            const int contenderCount = 32;

            var strategy = new RecordingHostCreationStrategy();
            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy));
            var request =
                CreateRequest(
                    "request-step-7e-high-contention");
            var candidate = CreateCandidate();

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        _ =>
                            Task.Run(
                                async () =>
                                {
                                    await startGate.Task;
                                    return await executor.ExecuteAsync(
                                        request,
                                        candidate);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            var created =
                Assert.Single(
                    results,
                    result => result.IsCreated);

            Assert.Equal(
                contenderCount - 1,
                results.Count(
                    result => result.IsDeduplicated));

            Assert.Equal(1, strategy.CallCount);

            Assert.All(
                results,
                result =>
                {
                    Assert.Equal(
                        created.PodUid,
                        result.PodUid);
                    Assert.Equal(
                        created.HostRequestId,
                        result.HostRequestId);
                    Assert.Equal(
                        created.PrimaryRuntimeInstanceId,
                        result.PrimaryRuntimeInstanceId);
                    Assert.Equal(
                        created.RuntimeInstanceIds.ToArray(),
                        result.RuntimeInstanceIds.ToArray());
                });
        }

        private static AiRuntimePoolPodCreationExecutor CreateExecutor(
            IAiRuntimeHostCreationStrategy strategy,
            IAiKubernetesRuntimePoolPodMembershipEnumerator membership,
            IAiRuntimeHostManager? runtimeHostManager = null)
        {
            var poolOptions =
                Options.Create(
                    new AiKubernetesRuntimePoolOptions
                    {
                        Enabled = true,
                        PoolId = "pool-step-7e",
                        RuntimeInstanceIdPrefix =
                            "runtime-step-7e",
                        ProviderName = "http",
                        TransportName = "http",
                        InitialRuntimeInstanceCount = 3,
                        MinimumRuntimeInstanceCount = 3,
                        MaximumRuntimeInstanceCount = 3
                    });

            var hostOptions =
                Options.Create(
                    new AiKubernetesRuntimePoolHostOptions
                    {
                        RuntimeImage = "runtime:test",
                        StartupTimeout =
                            TimeSpan.FromSeconds(1),
                        ReadinessPollInterval =
                            TimeSpan.FromMilliseconds(1)
                    });

            return runtimeHostManager is null
                ? new AiRuntimePoolPodCreationExecutor(
                    new[] { strategy },
                    membership,
                    poolOptions,
                    hostOptions)
                : new AiRuntimePoolPodCreationExecutor(
                    new[] { strategy },
                    membership,
                    poolOptions,
                    hostOptions,
                    runtimeHostManager);
        }

        private static AiRuntimeCapacitySelectionCandidate
            CreateCandidate()
        {
            return new AiRuntimeCapacitySelectionCandidate
            {
                Level =
                    AiRuntimeCapacitySelectionLevel
                        .RuntimePoolPodCreation,
                PoolId = "pool-step-7e",
                ProviderName = "http",
                IsCompatible = true,
                IsAvailable = true
            };
        }

        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string requestId)
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = requestId,
                ControlPlaneId = "control-plane-step-7e",
                SharedRunId = "shared-run-step-7e",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey:
                            string.Concat(
                                "step-7e:",
                                requestId),
                        project: "step-7e",
                        userId: "unit-test",
                        tenantId: "tenant-step-7e",
                        tenantGroupId:
                            "tenant-group-step-7e",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7e",
                TenantGroupId = "tenant-group-step-7e",
                RuntimeInstanceIdPrefix =
                    "runtime-step-7e",
                ProviderHint = "http",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 10,
                RequestedTargetInstanceCount = 1
            };
        }

        private sealed class RecordingHostCreationStrategy :
            IAiRuntimeHostCreationStrategy
        {
            private readonly bool reject;

            public RecordingHostCreationStrategy(
                bool reject = false)
            {
                this.reject = reject;
            }

            public int CallCount { get; private set; }

            public AiRuntimeHostStartRequest? LastRequest { get; private set; }

            public AiRuntimeHostCreationMode Mode =>
                AiRuntimeHostCreationMode.KubernetesPool;

            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.CallCount++;
                this.LastRequest = request;

                if (this.reject)
                {
                    return Task.FromResult(
                        AiRuntimeHostStartResult.Rejected(
                            request.ExecutionContextSnapshot,
                            request.RuntimeInstanceId,
                            request.ProviderName,
                            request.TransportName,
                            request.TransportEndpoint,
                            "pod-scheduling-unavailable",
                            retryable: true));
                }

                IReadOnlyDictionary<string, string> metadata =
                    new Dictionary<string, string>
                    {
                        [AiRuntimeHostMetadataKeys.HostId] =
                            "pod-uid-step-7e",
                        ["runtime.pool.id"] =
                            request.PoolId!,
                        ["kubernetes.pod.uid"] =
                            "pod-uid-step-7e"
                    };

                return Task.FromResult(
                    AiRuntimeHostStartResult.Started(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        "http://runtime-pool-step-7e",
                        metadata));
            }
        }

        private sealed class RecordingRuntimeHostManager :
            IAiRuntimeHostManager
        {
            private readonly IAiRuntimeHostCreationStrategy strategy;

            public RecordingRuntimeHostManager(
                IAiRuntimeHostCreationStrategy strategy)
            {
                this.strategy = strategy;
            }

            public int CallCount { get; private set; }

            public AiRuntimeHostStartRequest? LastRequest { get; private set; }

            public async Task<AiRuntimeHostStartResult> StartRuntimeAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                this.CallCount++;
                this.LastRequest = request;

                return await this.strategy
                    .StartAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private sealed class ReadyMembershipEnumerator :
            IAiKubernetesRuntimePoolPodMembershipEnumerator
        {
            private readonly RecordingHostCreationStrategy strategy;

            public ReadyMembershipEnumerator(
                RecordingHostCreationStrategy strategy)
            {
                this.strategy = strategy;
            }

            public Task<AiKubernetesRuntimePoolPodMembership>
                EnumerateAsync(
                    string poolId,
                    string podUid,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var primary =
                    this.strategy.LastRequest?.RuntimeInstanceId ??
                    throw new InvalidOperationException(
                        "The host strategy must execute before membership enumeration.");

                var members =
                    new[]
                    {
                        CreateMember(poolId, podUid, primary),
                        CreateMember(
                            poolId,
                            podUid,
                            "runtime-step-7e-secondary-1"),
                        CreateMember(
                            poolId,
                            podUid,
                            "runtime-step-7e-secondary-2")
                    };

                return Task.FromResult(
                    new AiKubernetesRuntimePoolPodMembership
                    {
                        PoolId = poolId,
                        PodUid = podUid,
                        EnumeratedAtUtc =
                            DateTimeOffset.UtcNow,
                        Members = members
                    });
            }

            private static AiKubernetesRuntimePoolPodMember
                CreateMember(
                    string poolId,
                    string podUid,
                    string runtimeInstanceId)
            {
                return new AiKubernetesRuntimePoolPodMember
                {
                    PoolId = poolId,
                    PodUid = podUid,
                    RuntimeInstanceId = runtimeInstanceId,
                    Status = AiRuntimeInstanceStatus.Ready,
                    CanAcceptRun = true,
                    RegisteredAtUtc =
                        DateTimeOffset.UtcNow,
                    LastHeartbeatAtUtc =
                        DateTimeOffset.UtcNow
                };
            }
        }
    }
}
