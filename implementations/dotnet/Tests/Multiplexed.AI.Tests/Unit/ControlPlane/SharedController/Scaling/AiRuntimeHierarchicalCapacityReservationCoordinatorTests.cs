using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Runtime.ControlPlane.Admission.Reservations;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Validates deterministic hierarchical selection combined with bounded atomic
    /// runtime-slot reservation.
    /// </summary>
    public sealed class AiRuntimeHierarchicalCapacityReservationCoordinatorTests
    {
        /// <summary>
        /// Verifies that concurrent selectors competing for the same first runtime
        /// converge on two distinct available runtime slots without over-reservation.
        /// </summary>
        [Fact]
        public async Task SelectAndReserveAsync_Should_Distribute_Concurrent_Reservations()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor("runtime-a"));

            await capacityStore.PublishAsync(
                CreateDescriptor("runtime-b"));

            var reservationStore =
                new CoordinatedAtomicReservationStore(
                    "runtime-a",
                    expectedContenderCount: 2);

            var coordinator =
                CreateCoordinator(
                    capacityStore,
                    reservationStore);

            var results =
                await Task.WhenAll(
                    coordinator.SelectAndReserveAsync(CreateRequest()),
                    coordinator.SelectAndReserveAsync(CreateRequest()));

            Assert.All(
                results,
                result => Assert.True(result.IsReserved));

            Assert.Equal(
                new[]
                {
                    "runtime-a",
                    "runtime-b"
                },
                results
                    .Select(result =>
                        result.Reservation!.RuntimeInstanceId)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray());

            Assert.Equal(
                1,
                await reservationStore.GetReservedRunCountAsync(
                    "runtime-a"));

            Assert.Equal(
                1,
                await reservationStore.GetReservedRunCountAsync(
                    "runtime-b"));

            Assert.Contains(
                results,
                result => result.SelectionAttemptCount == 2);
        }

        /// <summary>
        /// Verifies that concurrent selectors cannot over-reserve one runtime and that
        /// the losing selector converges on explicit backpressure.
        /// </summary>
        [Fact]
        public async Task SelectAndReserveAsync_Should_Backpressure_When_Only_Slot_Is_Contended()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor("runtime-a"));

            var reservationStore =
                new CoordinatedAtomicReservationStore(
                    "runtime-a",
                    expectedContenderCount: 2);

            var coordinator =
                CreateCoordinator(
                    capacityStore,
                    reservationStore);

            var results =
                await Task.WhenAll(
                    coordinator.SelectAndReserveAsync(CreateRequest()),
                    coordinator.SelectAndReserveAsync(CreateRequest()));

            var acquired =
                Assert.Single(
                    results,
                    result => result.IsReserved);

            var backpressure =
                Assert.Single(
                    results,
                    result => result.Decision.IsBackpressure);

            Assert.Equal(
                "runtime-a",
                acquired.Reservation!.RuntimeInstanceId);
            Assert.Null(backpressure.Reservation);
            Assert.Equal(2, backpressure.SelectionAttemptCount);
            Assert.Equal(
                1,
                await reservationStore.GetReservedRunCountAsync(
                    "runtime-a"));
        }

        /// <summary>
        /// Verifies that later process-capacity hierarchy decisions remain mutation-free
        /// and do not invoke runtime-slot reservation.
        /// </summary>
        [Fact]
        public async Task SelectAndReserveAsync_Should_Not_Reserve_Process_Creation_Decision()
        {
            var reservationStore =
                new ThrowingAtomicReservationStore();

            var coordinator =
                new AiRuntimeHierarchicalCapacityReservationCoordinator(
                    new FixedInventoryBuilder(
                        new AiRuntimeCapacitySelectionCandidate
                        {
                            Level =
                                AiRuntimeCapacitySelectionLevel
                                    .ExistingPoolPodProcessCreation,
                            PoolId = "pool-step-7c",
                            HostId = "host-step-7c",
                            ProviderName = "http",
                            IsCompatible = true,
                            IsAvailable = true,
                            AvailableProcessSlots = 1
                        }),
                    new AiRuntimeHierarchicalCapacitySelector(),
                    reservationStore);

            var result =
                await coordinator.SelectAndReserveAsync(
                    CreateRequest());

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel
                    .ExistingPoolPodProcessCreation,
                result.Decision.Level);
            Assert.False(result.IsReserved);
            Assert.Null(result.Reservation);
            Assert.Equal(0, reservationStore.TryReserveCallCount);
        }

        /// <summary>
        /// Verifies under high contention that one published runtime slot is acquired
        /// exactly once, every losing admission converges without over-reservation,
        /// and releasing the winning reservation makes the slot selectable again.
        /// </summary>
        [Fact]
        public async Task SelectAndReserveAsync_Should_Converge_Under_High_Contention()
        {
            const int contenderCount = 32;

            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor("runtime-a"));

            var reservationStore =
                new InMemoryAiRuntimeAdmissionReservationStore();

            var coordinator =
                CreateCoordinator(
                    capacityStore,
                    reservationStore);

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
                                    return await coordinator
                                        .SelectAndReserveAsync(
                                            CreateRequest());
                                }))
                    .ToArray();

            startGate.SetResult(true);

            var results =
                await Task.WhenAll(contenders);

            var acquired =
                Assert.Single(
                    results,
                    result => result.IsReserved);

            Assert.Equal(
                contenderCount - 1,
                results.Count(
                    result =>
                        result.Decision.IsBackpressure));

            Assert.Equal(
                "runtime-a",
                acquired.Reservation!.RuntimeInstanceId);

            Assert.Equal(
                1,
                await reservationStore.GetReservedRunCountAsync(
                    "runtime-a"));

            await reservationStore.ReleaseAsync(
                "runtime-a");

            var afterRelease =
                await coordinator.SelectAndReserveAsync(
                    CreateRequest());

            Assert.True(afterRelease.IsReserved);
            Assert.Equal(
                "runtime-a",
                afterRelease.Reservation!.RuntimeInstanceId);
            Assert.Equal(
                1,
                await reservationStore.GetReservedRunCountAsync(
                    "runtime-a"));
        }

        /// <summary>
        /// Creates the production Step 7C coordinator with focused in-memory
        /// authorities.
        /// </summary>
        /// <param name="capacityStore">The capacity store.</param>
        /// <param name="reservationStore">
        /// The bounded atomic reservation store.
        /// </param>
        /// <returns>The coordinator.</returns>
        private static AiRuntimeHierarchicalCapacityReservationCoordinator
            CreateCoordinator(
                IAiRuntimeInstanceCapacityStore capacityStore,
                IAiRuntimeAtomicAdmissionReservationStore reservationStore)
        {
            var inventoryBuilder =
                new AiRuntimeCapacitySelectionInventoryBuilder(
                    capacityStore,
                    new AllowAllVisibilityEvaluator(),
                    new EmptyCapacitySafetyReader(),
                    reservationStore);

            return new AiRuntimeHierarchicalCapacityReservationCoordinator(
                inventoryBuilder,
                new AiRuntimeHierarchicalCapacitySelector(),
                reservationStore);
        }

        /// <summary>
        /// Creates one authoritative pooled runtime capacity descriptor with one
        /// published run slot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = "pool-step-7c",
                HostId = "host-step-7c",
                ProviderName = "http",
                TenantGroupId = "tenant-group-step-7c",
                IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 1,
                AvailableWorkerCount = 1,
                MinWorkersRequiredPerRun = 1,
                MaxConcurrentRuns = 1,
                MaxRunSlots = 1,
                AvailableRunSlots = 1,
                EffectiveAvailableRunSlots = 1,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates the existing provider-level request used by Step 7C.
        /// </summary>
        /// <returns>The provider request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest()
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                ControlPlaneId = "step-7c-control-plane",
                SharedRunId = Guid.NewGuid().ToString("N"),
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey:
                            string.Concat(
                                "step-7c:",
                                Guid.NewGuid().ToString("N")),
                        project: "step-7c",
                        userId: "unit-test",
                        tenantId: "tenant-step-7c",
                        tenantGroupId: "tenant-group-step-7c",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7c",
                TenantGroupId = "tenant-group-step-7c",
                ProviderHint = "http",
                RequestedTargetInstanceCount = 1
            };
        }

        /// <summary>
        /// Provides unconditional visibility for focused Step 7C tests.
        /// </summary>
        private sealed class AllowAllVisibilityEvaluator :
            IAiRuntimeInstanceVisibilityEvaluator
        {
            /// <inheritdoc />
            public bool IsVisible(
                string? tenantId,
                string? tenantGroupId,
                AiRuntimeInstanceVisibilityDescriptor descriptor)
            {
                return true;
            }

            /// <inheritdoc />
            public AiRuntimeInstanceVisibilityDescriptor CreateDescriptor(
                string? runtimeInstanceId,
                IReadOnlyDictionary<string, string>? metadata)
            {
                throw new NotSupportedException(
                    "Step 7C uses first-class capacity visibility fields.");
            }
        }

        /// <summary>
        /// Provides an empty exact capacity suppression inventory.
        /// </summary>
        private sealed class EmptyCapacitySafetyReader :
            IAiRuntimePoolCapacitySafetyReader
        {
            /// <inheritdoc />
            public Task<AiRuntimePoolCapacitySuppression?> GetSuppressionAsync(
                string poolId,
                string hostId,
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiRuntimePoolCapacitySuppression?>(
                    null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<
                    IReadOnlyList<AiRuntimePoolCapacitySuppression>>(
                        Array.Empty<AiRuntimePoolCapacitySuppression>());
            }
        }

        /// <summary>
        /// Synchronizes the first concurrent attempts for one runtime before delegating
        /// to the production in-memory atomic reservation implementation.
        /// </summary>
        private sealed class CoordinatedAtomicReservationStore :
            IAiRuntimeAtomicAdmissionReservationStore
        {
            private readonly InMemoryAiRuntimeAdmissionReservationStore inner =
                new();
            private readonly string coordinatedRuntimeInstanceId;
            private readonly int expectedContenderCount;
            private readonly TaskCompletionSource<bool> contendersReady =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int contenderCount;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="CoordinatedAtomicReservationStore" /> class.
            /// </summary>
            /// <param name="coordinatedRuntimeInstanceId">
            /// The runtime whose first contention wave is synchronized.
            /// </param>
            /// <param name="expectedContenderCount">
            /// The expected number of concurrent contenders.
            /// </param>
            public CoordinatedAtomicReservationStore(
                string coordinatedRuntimeInstanceId,
                int expectedContenderCount)
            {
                this.coordinatedRuntimeInstanceId =
                    coordinatedRuntimeInstanceId;
                this.expectedContenderCount = expectedContenderCount;
            }

            /// <inheritdoc />
            public Task ReserveAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                return this.inner.ReserveAsync(
                    runtimeInstanceId,
                    runCount,
                    cancellationToken);
            }

            /// <inheritdoc />
            public async Task<AiRuntimeAdmissionReservationAttemptResult>
                TryReserveAsync(
                    string runtimeInstanceId,
                    int maximumReservedRunCount,
                    int runCount = 1,
                    CancellationToken cancellationToken = default)
            {
                if (StringComparer.Ordinal.Equals(
                        runtimeInstanceId,
                        this.coordinatedRuntimeInstanceId))
                {
                    var contender =
                        Interlocked.Increment(ref this.contenderCount);

                    if (contender <= this.expectedContenderCount)
                    {
                        if (contender == this.expectedContenderCount)
                        {
                            this.contendersReady.TrySetResult(true);
                        }

                        await this.contendersReady
                            .Task
                            .WaitAsync(cancellationToken);
                    }
                }

                return await this.inner.TryReserveAsync(
                    runtimeInstanceId,
                    maximumReservedRunCount,
                    runCount,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task ReleaseAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                return this.inner.ReleaseAsync(
                    runtimeInstanceId,
                    runCount,
                    cancellationToken);
            }

            /// <inheritdoc />
            public Task<int> GetReservedRunCountAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.inner.GetReservedRunCountAsync(
                    runtimeInstanceId,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Provides one fixed non-runtime hierarchy inventory.
        /// </summary>
        private sealed class FixedInventoryBuilder :
            IAiRuntimeCapacitySelectionInventoryBuilder
        {
            private readonly IReadOnlyList<AiRuntimeCapacitySelectionCandidate>
                candidates;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="FixedInventoryBuilder" /> class.
            /// </summary>
            /// <param name="candidate">The fixed candidate.</param>
            public FixedInventoryBuilder(
                AiRuntimeCapacitySelectionCandidate candidate)
            {
                this.candidates = new[] { candidate };
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeCapacitySelectionCandidate>>
                BuildAsync(
                    AiRuntimeScaleOutProviderRequest request,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(this.candidates);
            }
        }

        /// <summary>
        /// Fails the test if runtime reservation is attempted.
        /// </summary>
        private sealed class ThrowingAtomicReservationStore :
            IAiRuntimeAtomicAdmissionReservationStore
        {
            /// <summary>
            /// Gets the number of bounded reservation attempts.
            /// </summary>
            public int TryReserveCallCount { get; private set; }

            /// <inheritdoc />
            public Task ReserveAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeAdmissionReservationAttemptResult>
                TryReserveAsync(
                    string runtimeInstanceId,
                    int maximumReservedRunCount,
                    int runCount = 1,
                    CancellationToken cancellationToken = default)
            {
                this.TryReserveCallCount++;

                throw new InvalidOperationException(
                    "A non-runtime hierarchy decision must not reserve a run slot.");
            }

            /// <inheritdoc />
            public Task ReleaseAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<int> GetReservedRunCountAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0);
            }
        }
    }
}
