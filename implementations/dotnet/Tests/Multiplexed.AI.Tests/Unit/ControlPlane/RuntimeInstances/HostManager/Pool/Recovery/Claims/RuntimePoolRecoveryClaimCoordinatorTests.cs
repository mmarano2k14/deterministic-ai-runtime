using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Validates deterministic claims around exact assigned-work inventories.
    /// </summary>
    public sealed class RuntimePoolRecoveryClaimCoordinatorTests
    {
        /// <summary>
        /// Verifies one winner and one denied coordinator for the same exact A1 inventory.
        /// </summary>
        [Fact]
        public async Task TryAcquireAsync_Should_Claim_One_Exact_A1_Inventory()
        {
            var inventory =
                CreateInventory();

            var enumerator =
                new FakeAssignedWorkEnumerator(
                    inventory);

            var store =
                new InMemoryAiRuntimePoolRecoveryClaimStore();

            var coordinator =
                new AiRuntimePoolRecoveryClaimCoordinator(
                    enumerator,
                    store);

            var results =
                await Task.WhenAll(
                    coordinator.TryAcquireAsync(
                        "failure-a1",
                        "coordinator-01"),
                    coordinator.TryAcquireAsync(
                        "failure-a1",
                        "coordinator-02"));

            var acquired =
                Assert.Single(
                    results.Where(
                        result =>
                            result.Status ==
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .Acquired));

            var denied =
                Assert.Single(
                    results.Where(
                        result =>
                            result.Status ==
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .AlreadyClaimed));

            Assert.NotNull(acquired.Lease);
            Assert.Null(denied.Lease);

            Assert.Equal(
                acquired.Claim.ClaimId,
                denied.Claim.ClaimId);

            Assert.Equal(
                inventory.Candidates.Count,
                acquired.Claim.CandidateCount);

            Assert.Equal(
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(inventory),
                acquired.Claim.InventoryFingerprint);

            Assert.Equal(
                2,
                enumerator.CallCount);

            await acquired.Lease!.DisposeAsync();
        }

        /// <summary>
        /// Verifies that candidate order participates in the deterministic inventory fingerprint.
        /// </summary>
        [Fact]
        public void Fingerprint_Should_Change_When_Exact_Inventory_Order_Changes()
        {
            var inventory =
                CreateInventory();

            var reversed =
                inventory with
                {
                    Candidates =
                        inventory.Candidates
                            .Reverse()
                            .ToArray()
                };

            Assert.NotEqual(
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(inventory),
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(reversed));
        }

        /// <summary>
        /// Verifies that diagnostic metadata does not change claim authority.
        /// </summary>
        [Fact]
        public void Fingerprint_Should_Ignore_Diagnostic_Metadata()
        {
            var inventory =
                CreateInventory();

            var candidate =
                inventory.Candidates[0];

            var changed =
                inventory with
                {
                    Candidates =
                        new[]
                        {
                            candidate with
                            {
                                Metadata =
                                    new Dictionary<string, string>
                                    {
                                        ["diagnostic"] = "changed"
                                    }
                            },
                            inventory.Candidates[1]
                        }
                };

            Assert.Equal(
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(inventory),
                AiRuntimePoolRecoveryInventoryFingerprint
                    .Calculate(changed));
        }

        /// <summary>
        /// Creates one deterministic exact A1 inventory.
        /// </summary>
        private static AiRuntimePoolAssignedWorkInventory
            CreateInventory()
        {
            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = "failure-a1",
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = "runtime-a1",
                RouteId = "route-a1",
                EnumeratedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        5,
                        TimeSpan.Zero),
                Candidates =
                    new[]
                    {
                        new AiRuntimePoolAssignedWorkCandidate
                        {
                            FailureId = "failure-a1",
                            PoolId = "pool-01",
                            HostId = "host-01",
                            RuntimeInstanceId =
                                "runtime-a1",
                            RouteId = "route-a1",
                            LocalRunId =
                                "local-a1-flight",
                            ExecutionId =
                                "execution-a1",
                            Status = "running",
                            TenantId = "tenant-01",
                            TenantGroupId =
                                "tenant-group-01",
                            SharedRunId =
                                "shared-run-01",
                            Kind =
                                AiRuntimePoolAssignedWorkKind
                                    .InFlight,
                            CreatedAtUtc =
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    1,
                                    TimeSpan.Zero)
                        },
                        new AiRuntimePoolAssignedWorkCandidate
                        {
                            FailureId = "failure-a1",
                            PoolId = "pool-01",
                            HostId = "host-01",
                            RuntimeInstanceId =
                                "runtime-a1",
                            RouteId = "route-a1",
                            LocalRunId =
                                "local-a1-queued",
                            Status = "queued",
                            TenantId = "tenant-01",
                            TenantGroupId =
                                "tenant-group-01",
                            SharedRunId =
                                "shared-run-02",
                            Kind =
                                AiRuntimePoolAssignedWorkKind
                                    .LocalQueued,
                            CreatedAtUtc =
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    2,
                                    TimeSpan.Zero)
                        }
                    }
            };
        }

        /// <summary>
        /// Returns one deterministic exact assigned-work inventory.
        /// </summary>
        private sealed class FakeAssignedWorkEnumerator :
            IAiRuntimePoolAssignedWorkEnumerator
        {
            private readonly AiRuntimePoolAssignedWorkInventory
                inventory;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="FakeAssignedWorkEnumerator"/> class.
            /// </summary>
            public FakeAssignedWorkEnumerator(
                AiRuntimePoolAssignedWorkInventory inventory)
            {
                this.inventory = inventory;
            }

            /// <summary>
            /// Gets the number of read-only enumeration calls.
            /// </summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimePoolAssignedWorkInventory>
                EnumerateAsync(
                    string failureId,
                    CancellationToken cancellationToken = default)
            {
                this.CallCount++;

                Assert.Equal(
                    this.inventory.FailureId,
                    failureId);

                return Task.FromResult(
                    this.inventory);
            }
        }
    }
}
