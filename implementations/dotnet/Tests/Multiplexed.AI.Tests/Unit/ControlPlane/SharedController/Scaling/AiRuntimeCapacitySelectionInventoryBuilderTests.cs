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
    /// Validates projection of the existing runtime capacity store into Step 7
    /// hierarchical selection candidates.
    /// </summary>
    public sealed class AiRuntimeCapacitySelectionInventoryBuilderTests
    {
        /// <summary>
        /// Verifies that idle and active pooled runtime capacity is projected into the
        /// two existing runtime-level hierarchy positions.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Project_Warm_And_Existing_Runtime_Slots()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-busy",
                    AiRuntimeInstanceStatus.Busy,
                    availableRunSlots: 1,
                    runningRunCount: 1));

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-warm",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 2));

            var builder =
                CreateBuilder(capacityStore);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            Assert.Equal(2, candidates.Count);

            var busy =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-busy");

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel
                    .ExistingPoolRuntimeSlot,
                busy.Level);

            Assert.True(busy.IsCompatible);
            Assert.True(busy.IsAvailable);
            Assert.Equal(1, busy.AvailableRunSlots);

            var warm =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-warm");

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel
                    .CompatibleWarmRuntime,
                warm.Level);

            Assert.Equal(2, warm.AvailableRunSlots);
            Assert.Equal("http", warm.ProviderName);
        }

        /// <summary>
        /// Verifies that the existing tenant visibility authority is applied before
        /// runtime candidates enter the hierarchy inventory.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Apply_Tenant_Visibility()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-owned",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    tenantId: "tenant-step-7b",
                    isolationMode:
                        AiRuntimeInstanceIsolationMode.Dedicated));

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-foreign",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    tenantId: "tenant-other",
                    tenantGroupId: "tenant-group-other",
                    isolationMode:
                        AiRuntimeInstanceIsolationMode.Dedicated));

            var builder =
                CreateBuilder(capacityStore);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            var candidate = Assert.Single(candidates);

            Assert.Equal(
                "runtime-owned",
                candidate.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that missing typed pool, host, or provider identity is not repaired
        /// from diagnostic metadata.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Not_Infer_Identity_From_Metadata()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                new AiRuntimeInstanceCapacityDescriptor
                {
                    RuntimeInstanceId = "runtime-metadata-only",
                    Status = AiRuntimeInstanceStatus.Ready,
                    AvailableRunSlots = 1,
                    EffectiveAvailableRunSlots = 1,
                    AvailableWorkerCount = 1,
                    CanAcceptRun = true,
                    Metadata =
                        new Dictionary<string, string>
                        {
                            ["poolId"] = "pool-from-metadata",
                            ["hostId"] = "host-from-metadata",
                            ["provider.name"] = "http"
                        }
                });

            var builder =
                CreateBuilder(capacityStore);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            Assert.Empty(candidates);
        }

        /// <summary>
        /// Verifies that authoritative suppression and draining lifecycle evidence are
        /// preserved for deterministic exclusion by the selector.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Mark_Suppressed_And_Draining_Capacity()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            var suppressed =
                CreateDescriptor(
                    "runtime-suppressed",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1);

            var draining =
                CreateDescriptor(
                    "runtime-draining",
                    AiRuntimeInstanceStatus.Draining,
                    availableRunSlots: 1);

            await capacityStore.PublishAsync(suppressed);
            await capacityStore.PublishAsync(draining);

            var safetyReader =
                new FakeCapacitySafetyReader();

            safetyReader.Suppress(
                suppressed.PoolId!,
                suppressed.HostId!,
                suppressed.RuntimeInstanceId);

            var builder =
                CreateBuilder(
                    capacityStore,
                    safetyReader);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            var suppressedCandidate =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-suppressed");

            Assert.True(suppressedCandidate.IsSuppressed);
            Assert.False(suppressedCandidate.IsDraining);
            Assert.Equal(
                "runtime-capacity-suppressed",
                suppressedCandidate.Reason);

            var drainingCandidate =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-draining");

            Assert.True(drainingCandidate.IsDraining);
            Assert.False(drainingCandidate.IsAvailable);
            Assert.Equal(
                "runtime-capacity-draining",
                drainingCandidate.Reason);
        }

        /// <summary>
        /// Verifies that effective slots are derived from the existing admission
        /// reservation authority instead of duplicated heartbeat reservation fields.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Use_Authoritative_Admission_Reservations()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-partially-reserved",
                    AiRuntimeInstanceStatus.Busy,
                    availableRunSlots: 3,
                    reservedRunSlots: 99,
                    effectiveAvailableRunSlots: 0,
                    runningRunCount: 1));

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-fully-reserved",
                    AiRuntimeInstanceStatus.Busy,
                    availableRunSlots: 2,
                    reservedRunSlots: 0,
                    effectiveAvailableRunSlots: 2,
                    runningRunCount: 1));

            var reservationStore =
                new InMemoryAiRuntimeAdmissionReservationStore();

            await reservationStore.ReserveAsync(
                "runtime-partially-reserved",
                runCount: 2);

            await reservationStore.ReserveAsync(
                "runtime-fully-reserved",
                runCount: 2);

            var builder =
                CreateBuilder(
                    capacityStore,
                    reservationStore: reservationStore);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            var partial =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-partially-reserved");

            Assert.Equal(3, partial.PublishedAvailableRunSlots);
            Assert.Equal(2, partial.ReservedRunSlots);
            Assert.Equal(1, partial.AvailableRunSlots);
            Assert.True(partial.IsAvailable);

            var fullyReserved =
                Assert.Single(
                    candidates,
                    candidate =>
                        candidate.RuntimeInstanceId ==
                        "runtime-fully-reserved");

            Assert.Equal(2, fullyReserved.PublishedAvailableRunSlots);
            Assert.Equal(2, fullyReserved.ReservedRunSlots);
            Assert.Equal(0, fullyReserved.AvailableRunSlots);
            Assert.False(fullyReserved.IsAvailable);
        }

        /// <summary>
        /// Verifies that provider compatibility uses the typed descriptor provider and
        /// does not discard incompatible evidence from the projected inventory.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Mark_Typed_Provider_Mismatch()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-grpc",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    providerName: "grpc"));

            var builder =
                CreateBuilder(capacityStore);

            var candidate =
                Assert.Single(
                    await builder.BuildAsync(
                        CreateRequest(providerHint: "http")));

            Assert.False(candidate.IsCompatible);
            Assert.Equal(
                "runtime-provider-incompatible",
                candidate.Reason);
        }

        /// <summary>
        /// Verifies the Step 7A selector consumes the projected Step 7B inventory and
        /// skips an earlier suppressed warm runtime in favor of the next safe slot.
        /// </summary>
        [Fact]
        public async Task BuildAsync_And_Selector_Should_Select_Next_Safe_Runtime()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            var warm =
                CreateDescriptor(
                    "runtime-warm-suppressed",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1);

            var busy =
                CreateDescriptor(
                    "runtime-busy-safe",
                    AiRuntimeInstanceStatus.Busy,
                    availableRunSlots: 1,
                    runningRunCount: 1);

            await capacityStore.PublishAsync(warm);
            await capacityStore.PublishAsync(busy);

            var safetyReader =
                new FakeCapacitySafetyReader();

            safetyReader.Suppress(
                warm.PoolId!,
                warm.HostId!,
                warm.RuntimeInstanceId);

            var request = CreateRequest();
            var builder =
                CreateBuilder(
                    capacityStore,
                    safetyReader);

            var candidates =
                await builder.BuildAsync(request);

            var selector =
                new AiRuntimeHierarchicalCapacitySelector();

            var decision =
                await selector.SelectAsync(
                    request,
                    candidates);

            Assert.Equal(
                AiRuntimeCapacitySelectionLevel
                    .ExistingPoolRuntimeSlot,
                decision.Level);

            Assert.Equal(
                "runtime-busy-safe",
                decision.Candidate!.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that suppression evidence is loaded once per host instead of once
        /// per independently registered runtime member.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Load_Suppression_Inventory_Once_Per_Host()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-host-a-1",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    hostId: "host-step-7b-a"));

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-host-a-2",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    hostId: "host-step-7b-a"));

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-host-b-1",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    hostId: "host-step-7b-b"));

            var safetyReader =
                new FakeCapacitySafetyReader();

            var builder =
                CreateBuilder(
                    capacityStore,
                    safetyReader);

            var candidates =
                await builder.BuildAsync(CreateRequest());

            Assert.Equal(3, candidates.Count);
            Assert.Equal(2, safetyReader.ListByHostIdCallCount);
        }

        /// <summary>
        /// Verifies that tenant visibility uses the durable execution context snapshot
        /// instead of duplicated mutable request convenience fields.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Use_Durable_Snapshot_Tenant_Authority()
        {
            var capacityStore =
                new InMemoryAiRuntimeInstanceCapacityStore();

            await capacityStore.PublishAsync(
                CreateDescriptor(
                    "runtime-snapshot-owned",
                    AiRuntimeInstanceStatus.Ready,
                    availableRunSlots: 1,
                    tenantId: "tenant-step-7b",
                    isolationMode:
                        AiRuntimeInstanceIsolationMode.Dedicated));

            var request = CreateRequest();

            request.TenantId = "tenant-conflicting-convenience-field";
            request.TenantGroupId =
                "tenant-group-conflicting-convenience-field";

            var builder =
                CreateBuilder(capacityStore);

            var candidate =
                Assert.Single(
                    await builder.BuildAsync(request));

            Assert.Equal(
                "runtime-snapshot-owned",
                candidate.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that cancellation is observed before inventory access.
        /// </summary>
        [Fact]
        public async Task BuildAsync_Should_Observe_Cancellation()
        {
            var builder =
                CreateBuilder(
                    new InMemoryAiRuntimeInstanceCapacityStore());

            using var cancellation =
                new CancellationTokenSource();

            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                async () =>
                    await builder.BuildAsync(
                        CreateRequest(),
                        cancellation.Token));
        }

        /// <summary>
        /// Creates the production inventory builder with focused in-memory test
        /// authorities.
        /// </summary>
        /// <param name="capacityStore">The capacity store.</param>
        /// <param name="capacitySafetyReader">
        /// The optional exact capacity-safety reader.
        /// </param>
        /// <param name="reservationStore">
        /// The optional admission reservation authority.
        /// </param>
        /// <returns>The inventory builder.</returns>
        private static AiRuntimeCapacitySelectionInventoryBuilder CreateBuilder(
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimePoolCapacitySafetyReader? capacitySafetyReader = null,
            IAiRuntimeAdmissionReservationStore? reservationStore = null)
        {
            return new AiRuntimeCapacitySelectionInventoryBuilder(
                capacityStore,
                new FakeVisibilityEvaluator(),
                capacitySafetyReader ??
                new FakeCapacitySafetyReader(),
                reservationStore ??
                new InMemoryAiRuntimeAdmissionReservationStore());
        }

        /// <summary>
        /// Creates one authoritative pooled runtime capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="status">The runtime status.</param>
        /// <param name="availableRunSlots">The raw available run slots.</param>
        /// <param name="reservedRunSlots">The reserved run slots.</param>
        /// <param name="effectiveAvailableRunSlots">
        /// The effective available run slots.
        /// </param>
        /// <param name="runningRunCount">The running run count.</param>
        /// <param name="tenantId">The owning tenant identifier.</param>
        /// <param name="tenantGroupId">The owning tenant group identifier.</param>
        /// <param name="isolationMode">The runtime isolation mode.</param>
        /// <param name="providerName">The typed provider name.</param>
        /// <param name="poolId">The first-class runtime pool identifier.</param>
        /// <param name="hostId">The first-class host incarnation identifier.</param>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor(
            string runtimeInstanceId,
            AiRuntimeInstanceStatus status,
            int availableRunSlots,
            int reservedRunSlots = 0,
            int? effectiveAvailableRunSlots = null,
            int runningRunCount = 0,
            string? tenantId = null,
            string? tenantGroupId = "tenant-group-step-7b",
            AiRuntimeInstanceIsolationMode isolationMode =
                AiRuntimeInstanceIsolationMode.Shared,
            string providerName = "http",
            string poolId = "pool-step-7b",
            string hostId = "host-step-7b")
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = poolId,
                HostId = hostId,
                ProviderName = providerName,
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                IsolationMode = isolationMode,
                AllowSharedFallback = true,
                PreferDedicatedCapacity =
                    isolationMode !=
                    AiRuntimeInstanceIsolationMode.Shared,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = status,
                WorkerCount = 3,
                ActiveWorkerCount = runningRunCount,
                AvailableWorkerCount =
                    Math.Max(
                        1,
                        3 - runningRunCount),
                MinWorkersRequiredPerRun = 1,
                RunningRunCount = runningRunCount,
                ActiveRunCount = runningRunCount,
                MaxRunSlots = 3,
                AvailableRunSlots = availableRunSlots,
                ReservedRunSlots = reservedRunSlots,
                EffectiveAvailableRunSlots =
                    effectiveAvailableRunSlots,
                CanAcceptRun =
                    status is
                        AiRuntimeInstanceStatus.Ready or
                        AiRuntimeInstanceStatus.Busy,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata =
                    new Dictionary<string, string>
                    {
                        ["diagnostic"] = runtimeInstanceId
                    }
            };
        }

        /// <summary>
        /// Creates the existing provider-level request used by Steps 7A and 7B.
        /// </summary>
        /// <param name="providerHint">The provider hint.</param>
        /// <returns>The request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest(
            string providerHint = "http")
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = "step-7b-request",
                ControlPlaneId = "step-7b-control-plane",
                SharedRunId = "step-7b-shared-run",
                ExecutionContextSnapshot =
                    AiExecutionContextSnapshotTestFactory.Create(
                        contextKey: "step-7b:tenant:context",
                        project: "step-7b",
                        userId: "unit-test",
                        tenantId: "tenant-step-7b",
                        tenantGroupId: "tenant-group-step-7b",
                        currentNamespace: "unit-test"),
                TenantId = "tenant-step-7b",
                TenantGroupId = "tenant-group-step-7b",
                IsolationMode =
                    AiRuntimeInstanceIsolationMode.Hybrid,
                AllowSharedFallback = true,
                ProviderHint = providerHint,
                RequestedTargetInstanceCount = 1
            };
        }

        /// <summary>
        /// Provides deterministic tenant visibility for the focused inventory tests.
        /// </summary>
        private sealed class FakeVisibilityEvaluator :
            IAiRuntimeInstanceVisibilityEvaluator
        {
            /// <inheritdoc />
            public bool IsVisible(
                string? tenantId,
                string? tenantGroupId,
                AiRuntimeInstanceVisibilityDescriptor descriptor)
            {
                if (descriptor.IsolationMode ==
                    AiRuntimeInstanceIsolationMode.Shared)
                {
                    return true;
                }

                return StringComparer.OrdinalIgnoreCase.Equals(
                           tenantId,
                           descriptor.TenantId) ||
                       StringComparer.OrdinalIgnoreCase.Equals(
                           tenantGroupId,
                           descriptor.TenantGroupId);
            }

            /// <inheritdoc />
            public AiRuntimeInstanceVisibilityDescriptor CreateDescriptor(
                string? runtimeInstanceId,
                IReadOnlyDictionary<string, string>? metadata)
            {
                throw new NotSupportedException(
                    "Step 7B uses first-class capacity isolation fields.");
            }
        }

        /// <summary>
        /// Provides exact in-memory suppression evidence for focused inventory tests.
        /// </summary>
        private sealed class FakeCapacitySafetyReader :
            IAiRuntimePoolCapacitySafetyReader
        {
            private readonly Dictionary<
                (string PoolId, string HostId, string RuntimeInstanceId),
                AiRuntimePoolCapacitySuppression> suppressions =
                    new();

            /// <summary>
            /// Gets the number of host suppression inventory reads.
            /// </summary>
            public int ListByHostIdCallCount { get; private set; }

            /// <summary>
            /// Adds one exact runtime capacity suppression.
            /// </summary>
            /// <param name="poolId">The pool identifier.</param>
            /// <param name="hostId">The host identifier.</param>
            /// <param name="runtimeInstanceId">
            /// The runtime instance identifier.
            /// </param>
            public void Suppress(
                string poolId,
                string hostId,
                string runtimeInstanceId)
            {
                this.suppressions[
                    (poolId, hostId, runtimeInstanceId)] =
                        new AiRuntimePoolCapacitySuppression
                        {
                            FailureId =
                                string.Concat(
                                    "failure-",
                                    runtimeInstanceId),
                            Scope =
                                AiRuntimePoolCapacitySuppressionScope
                                    .RuntimeInstanceRoute,
                            PoolId = poolId,
                            HostId = hostId,
                            RuntimeInstanceId = runtimeInstanceId,
                            SuppressedAtUtc = DateTimeOffset.UtcNow
                        };
            }

            /// <inheritdoc />
            public Task<AiRuntimePoolCapacitySuppression?>
                GetSuppressionAsync(
                    string poolId,
                    string hostId,
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.suppressions.TryGetValue(
                    (poolId, hostId, runtimeInstanceId),
                    out var suppression);

                return Task.FromResult(suppression);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
                ListByHostIdAsync(
                    string hostId,
                    CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.ListByHostIdCallCount++;

                IReadOnlyList<AiRuntimePoolCapacitySuppression> result =
                    this.suppressions
                        .Values
                        .Where(suppression =>
                            StringComparer.Ordinal.Equals(
                                suppression.HostId,
                                hostId))
                        .OrderBy(
                            suppression =>
                                suppression.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(result);
            }
        }
    }
}
