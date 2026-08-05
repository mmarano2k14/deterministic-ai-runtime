using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Client;
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
        /// Verifies that the canonical Pod creation path preserves an explicit
        /// zero-capacity local runtime queue.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Preserve_Explicit_Zero_LocalQueueCapacity()
        {
            var strategy = new RecordingHostCreationStrategy();
            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy));

            var request =
                CreateRequest(
                    "request-step-7e-zero-local-queue");

            request.LocalQueueCapacity = 0;

            var result =
                await executor.ExecuteAsync(
                    request,
                    CreateCandidate());

            Assert.True(result.IsCreated);
            Assert.NotNull(strategy.LastRequest);
            Assert.Equal(
                0,
                strategy.LastRequest!.LocalQueueCapacity);
        }

        /// <summary>
        /// Verifies that distinct concurrent reservation attempts are atomically
        /// bounded by the configured physical Pod limit.
        /// </summary>
        [Fact]
        public async Task ReservationStore_Should_Atomically_Bound_Concurrent_Distinct_Pod_Reservations()
        {
            const int maximumPodCount = 3;
            const int contenderCount = 32;

            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            var startGate =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var contenders =
                Enumerable
                    .Range(0, contenderCount)
                    .Select(
                        ordinal =>
                            Task.Run(
                                async () =>
                                {
                                    await startGate.Task;

                                    return await reservations.TryAcquireAsync(
                                            "control-plane-step-7e",
                                            "pool-step-7e",
                                            string.Concat(
                                                "pod-reservation-",
                                                ordinal),
                                            activePodCount: 0,
                                            maximumPodCount:
                                                maximumPodCount,
                                            expiresAtUtc:
                                                DateTimeOffset.UtcNow
                                                    .AddMinutes(1))
                                        .ConfigureAwait(false);
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            Assert.Equal(
                maximumPodCount,
                results.Count(result => result.Acquired));

            Assert.All(
                results,
                result =>
                {
                    Assert.InRange(
                        result.ReservedPodCount,
                        1,
                        maximumPodCount);
                    Assert.Equal(
                        maximumPodCount,
                        result.MaximumPodCount);
                });
        }

        /// <summary>
        /// Verifies that active Pods plus in-flight Pod reservations never exceed
        /// the configured physical Pod limit.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Not_Create_Fourth_Pod_When_Three_Pod_Slots_Are_Consumed()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-1",
                    "runtime-existing-1")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-2",
                    "runtime-existing-2")
                .ConfigureAwait(false);

            var held =
                await reservations.TryAcquireAsync(
                        "control-plane-step-7e",
                        "pool-step-7e",
                        "pod-3-in-flight",
                        activePodCount: 2,
                        maximumPodCount: 3,
                        expiresAtUtc:
                            DateTimeOffset.UtcNow.AddMinutes(1))
                    .ConfigureAwait(false);

            Assert.True(held.Acquired);

            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(2),
                    maximumPodCount: 3);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest("request-step-7e-pod-4"),
                    CreateCandidate());

            Assert.True(result.IsCapacityAlreadySatisfied);
            Assert.Equal(2, result.ActivePodCount);
            Assert.Equal(1, result.ReservedPodCreationCount);
            Assert.Equal(3, result.MaximumPodCount);
            Assert.Equal(0, strategy.CallCount);
            Assert.Contains(
                result.PrimaryRuntimeInstanceId,
                new[]
                {
                    "runtime-existing-1",
                    "runtime-existing-2"
                });
        }

        /// <summary>
        /// Verifies that the already control-plane-scoped pool membership authority
        /// is not filtered a second time with the scale-out request identifier.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Count_Authoritative_Pool_Membership_Without_Second_ControlPlane_Filter()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-1",
                    "runtime-existing-1",
                    controlPlaneId:
                        "registry-scoped-control-plane")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-2",
                    "runtime-existing-2",
                    controlPlaneId:
                        "registry-scoped-control-plane")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-3",
                    "runtime-existing-3",
                    controlPlaneId:
                        "registry-scoped-control-plane")
                .ConfigureAwait(false);

            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(3),
                    maximumPodCount: 3);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest(
                        "request-step-7e-no-second-control-plane-filter"),
                    CreateCandidate());

            Assert.True(result.IsCapacityAlreadySatisfied);
            Assert.Equal(3, result.ActivePodCount);
            Assert.Equal(0, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that physical Kubernetes inventory prevents a fourth Pod even
        /// when runtime registry membership temporarily exposes only two hosts.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Use_Physical_Pod_Inventory_When_Registry_Undercounts()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-1",
                    "runtime-existing-1")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-2",
                    "runtime-existing-2")
                .ConfigureAwait(false);

            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(3),
                    maximumPodCount: 3);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest(
                        "request-step-7e-physical-inventory-cap"),
                    CreateCandidate());

            Assert.True(result.IsCapacityAlreadySatisfied);
            Assert.Equal(3, result.ActivePodCount);
            Assert.Equal(0, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that one free physical Pod slot permits exact Pod creation.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Create_When_One_Pod_Slot_Is_Free()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-1",
                    "runtime-existing-1")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-2",
                    "runtime-existing-2")
                .ConfigureAwait(false);

            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(2),
                    maximumPodCount: 3);

            var result =
                await executor.ExecuteAsync(
                    CreateRequest("request-step-7e-pod-3"),
                    CreateCandidate());

            Assert.True(result.IsCreated);
            Assert.Equal(1, strategy.CallCount);
        }

        /// <summary>
        /// Verifies that a created Pod whose exact membership does not converge
        /// retains conservative shadow capacity until reservation expiry.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Retain_Reservation_When_Created_Pod_Does_Not_Converge()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            var executor =
                CreateExecutor(
                    strategy,
                    new MissingMembershipEnumerator(),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(0),
                    maximumPodCount: 1,
                    startupTimeout:
                        TimeSpan.FromMilliseconds(20));

            var result =
                await executor.ExecuteAsync(
                    CreateRequest(
                        "request-step-7e-membership-timeout"),
                    CreateCandidate());

            Assert.Equal(
                AiRuntimePoolPodCreationStatus.Rejected,
                result.Status);
            Assert.Equal(1, strategy.CallCount);

            var nextReservation =
                await reservations.TryAcquireAsync(
                        "control-plane-step-7e",
                        "pool-step-7e",
                        "replacement-before-shadow-expiry",
                        activePodCount: 0,
                        maximumPodCount: 1,
                        expiresAtUtc:
                            DateTimeOffset.UtcNow.AddMinutes(1))
                    .ConfigureAwait(false);

            Assert.False(nextReservation.Acquired);
            Assert.Equal(1, nextReservation.ReservedPodCount);
        }

        /// <summary>
        /// Verifies that an exact recovery replacement remains governed by the
        /// existing recovery claim authority instead of normal scale-out Pod quota.
        /// </summary>
        [Fact]
        public async Task ExecuteAsync_Should_Not_Suppress_Recovery_Replacement_At_Normal_Pod_Limit()
        {
            var strategy = new RecordingHostCreationStrategy();
            var registry = new InMemoryAiRuntimeInstanceRegistry();
            var reservations =
                new InMemoryAiRuntimePoolPodCreationReservationStore();

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-1",
                    "runtime-existing-1")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-2",
                    "runtime-existing-2")
                .ConfigureAwait(false);

            await RegisterPoolPodAsync(
                    registry,
                    "pod-uid-3",
                    "runtime-existing-3")
                .ConfigureAwait(false);

            var request =
                CreateRequest(
                    "request-step-7e-recovery-replacement");

            request.Metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["scaleout.excludedRuntimeInstanceId"] =
                        "runtime-existing-3",
                    ["scaleout.replacementForRuntimeInstanceId"] =
                        "runtime-existing-3"
                };

            var executor =
                CreateExecutor(
                    strategy,
                    new ReadyMembershipEnumerator(strategy),
                    runtimePoolMembershipReader: registry,
                    reservationStore: reservations,
                    physicalPodInventory:
                        new FixedPhysicalPodInventory(3),
                    maximumPodCount: 3);

            var result =
                await executor.ExecuteAsync(
                    request,
                    CreateCandidate());

            Assert.True(result.IsCreated);
            Assert.Equal(1, strategy.CallCount);
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
            IAiRuntimeHostManager? runtimeHostManager = null,
            IAiRuntimePoolMembershipReader?
                runtimePoolMembershipReader = null,
            IAiRuntimePoolPodCreationReservationStore?
                reservationStore = null,
            IAiKubernetesRuntimePoolPodInventory?
                physicalPodInventory = null,
            int maximumPodCount = int.MaxValue,
            TimeSpan? startupTimeout = null)
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
                        MaximumPodCount = maximumPodCount,
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
                            startupTimeout ??
                            TimeSpan.FromSeconds(1),
                        ReadinessPollInterval =
                            TimeSpan.FromMilliseconds(1)
                    });

            if (runtimePoolMembershipReader is not null &&
                reservationStore is not null &&
                physicalPodInventory is not null)
            {
                return new AiRuntimePoolPodCreationExecutor(
                    new[] { strategy },
                    membership,
                    poolOptions,
                    hostOptions,
                    runtimePoolMembershipReader,
                    reservationStore,
                    physicalPodInventory,
                    runtimeHostManager ??
                        new RecordingRuntimeHostManager(strategy));
            }

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

        private static async Task RegisterPoolPodAsync(
            IAiRuntimeInstanceRegistry registry,
            string hostId,
            string runtimeInstanceId,
            string controlPlaneId = "control-plane-step-7e")
        {
            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        ControlPlaneId = controlPlaneId,
                        ControlPlaneHostId =
                            "control-plane-host-step-7e",
                        PoolId = "pool-step-7e",
                        HostId = hostId,
                        RuntimeId = runtimeInstanceId,
                        TenantId = "tenant-step-7e",
                        TenantGroupId =
                            "tenant-group-step-7e",
                        Role = AiRuntimeInstanceRole.Runtime,
                        WorkerCount = 1,
                        MaxConcurrentRuns = 1,
                        QueueCapacity = 0,
                        RegisteredAtUtc = DateTimeOffset.UtcNow
                    })
                .ConfigureAwait(false);
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

        private sealed class FixedPhysicalPodInventory :
            IAiKubernetesRuntimePoolPodInventory
        {
            private readonly int physicalPodCount;

            public FixedPhysicalPodInventory(
                int physicalPodCount)
            {
                this.physicalPodCount = physicalPodCount;
            }

            public Task<int> CountRuntimePoolPodsAsync(
                string namespaceName,
                string poolId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.physicalPodCount);
            }
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

        private sealed class MissingMembershipEnumerator :
            IAiKubernetesRuntimePoolPodMembershipEnumerator
        {
            public Task<AiKubernetesRuntimePoolPodMembership>
                EnumerateAsync(
                    string poolId,
                    string podUid,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                throw new AiKubernetesRuntimePoolPodMembershipAuthorityException(
                        poolId,
                        podUid,
                        AiKubernetesRuntimePoolPodMembershipAuthorityFailure
                            .MembershipNotFound,
                        "The test Pod membership is not registered.");
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
